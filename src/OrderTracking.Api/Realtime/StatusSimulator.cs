using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderTracking.Domain.Orders;
using OrderTracking.Infrastructure;
using OrderTracking.Infrastructure.Persistence;

namespace OrderTracking.Api.Realtime;

/// <summary>
/// Creates orders and moves them along, so a running instance has something to show.
/// </summary>
/// <param name="scopeFactory">Creates a scope per action.</param>
/// <param name="timeProvider">Clock used for timestamps and for pacing.</param>
/// <param name="options">Simulator settings.</param>
/// <param name="logger">Receives what the simulator did.</param>
/// <remarks>
/// <para>
/// Registered only when enabled, so in every other configuration this class is not
/// constructed at all rather than constructed and then asked to do nothing.
/// </para>
/// <para>
/// It goes through the domain and the ordinary persistence path, with no shortcut that writes
/// rows directly, so the events it produces travel the same outbox, broker and socket the real
/// ones do.
/// </para>
/// </remarks>
public sealed partial class StatusSimulator(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<StatusSimulatorOptions> options,
    ILogger<StatusSimulator> logger) : BackgroundService
{
    private static readonly string[] _catalogue =
    [
        "Кабель ВВГнг-LS 3x2.5, 200 м",
        "Щит распределительный IP54, 24 модуля",
        "Автоматический выключатель C16, 30 шт",
        "Светильник светодиодный 36 Вт, 48 шт",
        "Лоток кабельный оцинкованный 100x50, 60 м",
        "Гофротруба ПВХ 25 мм, 500 м",
        "УЗО 40 А / 30 мА, 12 шт",
        "Розетка силовая 32 А, 16 шт",
        "Стяжка кабельная 300 мм, 2000 шт",
        "Шкаф телекоммуникационный 19\", 42U"
    ];

    private readonly StatusSimulatorOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Interval, timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await StepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A demo aid must never be able to bring the application down.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogStepFailed(logger, exception);
            }
        }
    }

    private async Task StepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();

        var open = await dbContext.Orders
            .Where(order => order.Status == OrderStatus.Created || order.Status == OrderStatus.Shipped)
            .OrderBy(order => order.UpdatedAt)
            .Take(_options.MaxOpenOrders)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Advance the order that has been waiting longest, unless there is room for another
        // one — which keeps the screen showing both new arrivals and progress.
        if (open.Count >= _options.MaxOpenOrders || (open.Count > 0 && Random.Shared.Next(3) != 0))
        {
            await AdvanceAsync(dbContext, open[0], cancellationToken).ConfigureAwait(false);
            return;
        }

        await CreateAsync(scope, dbContext, cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateAsync(
        AsyncServiceScope scope, OrderTrackingDbContext dbContext, CancellationToken cancellationToken)
    {
        var generator = scope.ServiceProvider.GetRequiredService<IOrderNumberGenerator>();
        var orderNumber = await generator.NextAsync(cancellationToken).ConfigureAwait(false);

        var order = Order.Create(
            orderNumber,
            _catalogue[Random.Shared.Next(_catalogue.Length)],
            timeProvider.GetStorableUtcNow());

        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogCreated(logger, orderNumber);
    }

    private async Task AdvanceAsync(
        OrderTrackingDbContext dbContext, Order order, CancellationToken cancellationToken)
    {
        var allowed = OrderStatusTransitions.AllowedFrom(order.Status).ToArray();

        if (allowed.Length == 0)
        {
            return;
        }

        // Cancellation is possible but rare, so the happy path is what a viewer mostly sees
        // while the unhappy one still shows up often enough to be worth rendering.
        var next = allowed.Length > 1 && Random.Shared.Next(6) == 0
            ? allowed[^1]
            : allowed[0];

        var from = order.Status;
        order.ChangeStatus(next, timeProvider.GetStorableUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        LogAdvanced(logger, order.OrderNumber, from, next);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Simulator created {OrderNumber}.")]
    private static partial void LogCreated(ILogger logger, string orderNumber);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Simulator moved {OrderNumber} from {From} to {To}.")]
    private static partial void LogAdvanced(ILogger logger, string orderNumber, OrderStatus from, OrderStatus to);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Simulator step failed.")]
    private static partial void LogStepFailed(ILogger logger, Exception exception);
}

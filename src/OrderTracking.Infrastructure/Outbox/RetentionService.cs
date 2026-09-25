using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderTracking.Infrastructure.Diagnostics;
using OrderTracking.Infrastructure.Persistence;

namespace OrderTracking.Infrastructure.Outbox;

/// <summary>
/// Deletes outbox rows and deduplication markers that are past their retention period.
/// </summary>
/// <param name="scopeFactory">Creates a scope per sweep, since this service is a singleton.</param>
/// <param name="timeProvider">Clock, injected so tests are not forced to wait in real time.</param>
/// <param name="options">Retention periods and sweep interval.</param>
/// <param name="logger">Receives what was deleted.</param>
/// <remarks>
/// Runs in the API process alongside the publisher. A separate job would be tidier and is
/// what a real deployment would use; here it would mean a second deployable for one DELETE.
/// </remarks>
public sealed partial class RetentionService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<RetentionOptions> options,
    ILogger<RetentionService> logger) : BackgroundService
{
    private readonly RetentionOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        // The timer waits a full interval before the first tick. Startup is the busiest moment
        // a process has, and nothing here is urgent enough to compete with it.
        using var timer = new PeriodicTimer(_options.Interval, timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Housekeeping must never take the application down.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogSweepFailed(logger, exception);
            }
        }
    }

    /// <summary>
    /// Runs one sweep immediately.
    /// </summary>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>A task that completes when the sweep has finished.</returns>
    /// <remarks>
    /// Public because the loop above is only a schedule. Separating "when to run" from "what
    /// to do" lets a test assert on the deletion without waiting out an interval, and leaves
    /// room for an operator endpoint that forces a sweep.
    /// </remarks>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();

        var now = timeProvider.GetUtcNow();

        var outboxDeleted = await DeleteInBatchesAsync(
            () => dbContext.OutboxMessages
                .Where(message => message.ProcessedAt != null
                                  && message.ProcessedAt < now - _options.OutboxPeriod)
                .OrderBy(message => message.Id)
                .Take(_options.BatchSize),
            cancellationToken).ConfigureAwait(false);

        var markersDeleted = await DeleteInBatchesAsync(
            () => dbContext.ProcessedMessages
                .Where(message => message.ProcessedAt < now - _options.ProcessedMessagePeriod)
                .OrderBy(message => message.MessageId)
                .Take(_options.BatchSize),
            cancellationToken).ConfigureAwait(false);

        OrderTrackingDiagnostics.RetentionDeleted("outbox_messages", outboxDeleted);
        OrderTrackingDiagnostics.RetentionDeleted("processed_messages", markersDeleted);

        if (outboxDeleted + markersDeleted > 0)
        {
            LogSwept(logger, outboxDeleted, markersDeleted);
        }
    }

    /// <summary>
    /// Deletes everything the query selects, a batch at a time.
    /// </summary>
    /// <param name="batch">Produces the next batch; re-evaluated after each delete.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>How many rows were deleted in total.</returns>
    private static async Task<int> DeleteInBatchesAsync<TEntity>(
        Func<IQueryable<TEntity>> batch,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var total = 0;

        while (true)
        {
            // ExecuteDeleteAsync issues a DELETE rather than loading the rows to delete them,
            // so nothing is materialized and the change tracker stays empty.
            var deleted = await batch().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

            total += deleted;

            if (deleted == 0)
            {
                return total;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Retention removed {OutboxRows} outbox row(s) and {MarkerRows} deduplication marker(s).")]
    private static partial void LogSwept(ILogger logger, int outboxRows, int markerRows);

    [LoggerMessage(Level = LogLevel.Error, Message = "Retention sweep failed.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Retention is switched off; outbox rows and deduplication markers are kept indefinitely.")]
    private static partial void LogDisabled(ILogger logger);
}

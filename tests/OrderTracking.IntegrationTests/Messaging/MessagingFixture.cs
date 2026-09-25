using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderTracking.Infrastructure;
using OrderTracking.Infrastructure.Messaging;
using OrderTracking.Infrastructure.Persistence;
using RabbitMQ.Client;

namespace OrderTracking.IntegrationTests.Messaging;

/// <summary>
/// Runs the outbox publisher and the event consumer against a real broker and database.
/// </summary>
/// <remarks>
/// <para>
/// No web host. The chain under test starts at <c>SaveChangesAsync</c>, and putting HTTP in
/// front of it would only add a way for the test to fail for reasons the endpoint tests
/// already cover.
/// </para>
/// <para>
/// Every fixture instance gets its own exchange and queue names, because the classes in this
/// assembly run in parallel against one broker.
/// </para>
/// </remarks>
/// <param name="environment">The assembly-wide PostgreSQL and RabbitMQ containers.</param>
public sealed class MessagingFixture(ContainerEnvironment environment) : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    private ServiceProvider _provider = null!;
    private IHostedService[] _hostedServices = [];

    /// <summary>Where the pipeline ends, and what the tests assert against.</summary>
    public RecordingOrderEventHandler Handler { get; } = new();

    /// <summary>Creates a scope holding a context bound to the test database.</summary>
    /// <returns>A new scope; dispose it to dispose the context.</returns>
    public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var amqp = environment.AmqpUri;
        var connectionString = await environment.CreateDatabaseAsync("messaging");

        var services = new ServiceCollection();

        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(TimeProvider.System);
        services.AddOrderTrackingInfrastructure(connectionString);

        services.AddOrderTrackingMessaging(
            options =>
            {
                options.HostName = amqp.Host;
                options.Port = amqp.Port;
                (options.UserName, options.Password) = SplitUserInfo(amqp);
                options.Exchange = $"test-{_suffix}";
                options.Queue = $"test-{_suffix}.notifications";
                options.DeadLetterExchange = $"test-{_suffix}.dlx";
                options.DeadLetterQueue = $"test-{_suffix}.dlq";
            },
            // Faster than production so the tests are not mostly spent waiting. The interval
            // is a setting precisely so this is a configuration change, not a code path that
            // only exists for tests.
            outbox => outbox.PollingInterval = TimeSpan.FromMilliseconds(100));

        // The one substitution: the pipeline has to end somewhere a test can look.
        services.AddSingleton(Handler);
        services.AddScoped<IOrderEventHandler>(
            provider => provider.GetRequiredService<RecordingOrderEventHandler>());

        _provider = services.BuildServiceProvider();

        await using (var scope = CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();
            await TestDatabase.ResetAsync(dbContext, cancellationToken);
        }

        _hostedServices = [.. _provider.GetServices<IHostedService>()];

        foreach (var hostedService in _hostedServices)
        {
            await hostedService.StartAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var hostedService in _hostedServices)
        {
            await hostedService.StopAsync(CancellationToken.None);
        }

        await DeleteTopologyAsync();
        await _provider.DisposeAsync();
    }

    private async Task DeleteTopologyAsync()
    {
        var options = _provider.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
        var connection = _provider.GetRequiredService<RabbitMqConnection>();

        var amqp = await connection.GetAsync(CancellationToken.None);
        await using var channel = await amqp.CreateChannelAsync();

        await channel.QueueDeleteAsync(options.Queue);
        await channel.QueueDeleteAsync(options.DeadLetterQueue);
        await channel.ExchangeDeleteAsync(options.Exchange);
        await channel.ExchangeDeleteAsync(options.DeadLetterExchange);
    }

    private static (string UserName, string Password) SplitUserInfo(Uri amqp)
    {
        var separator = amqp.UserInfo.IndexOf(':', StringComparison.Ordinal);

        return separator < 0
            ? (Uri.UnescapeDataString(amqp.UserInfo), string.Empty)
            : (Uri.UnescapeDataString(amqp.UserInfo[..separator]),
               Uri.UnescapeDataString(amqp.UserInfo[(separator + 1)..]));
    }
}

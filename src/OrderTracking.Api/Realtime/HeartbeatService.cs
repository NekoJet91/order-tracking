using Microsoft.Extensions.Options;

namespace OrderTracking.Api.Realtime;

/// <summary>
/// Sends a heartbeat frame to every connected client on a fixed interval.
/// </summary>
/// <param name="connections">The clients to reach.</param>
/// <param name="timeProvider">Clock used to stamp the frame and to time the interval.</param>
/// <param name="options">Socket tuning.</param>
/// <remarks>
/// One timer for all connections rather than one per connection. The frames are identical
/// and are serialized once, so the cost of a heartbeat does not grow with the number of
/// clients the way a timer each would.
/// </remarks>
public sealed class HeartbeatService(
    OrderSocketConnectionManager connections,
    TimeProvider timeProvider,
    IOptions<OrderSocketOptions> options) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.HeartbeatInterval, timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (connections.Count > 0)
            {
                connections.Heartbeat(timeProvider.GetUtcNow());
            }
        }
    }
}

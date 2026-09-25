using System.Collections.Concurrent;
using OrderTracking.Infrastructure.Messaging;

namespace OrderTracking.IntegrationTests.Messaging;

/// <summary>
/// Stands in for the real handler and remembers everything the consumer delivered.
/// </summary>
/// <remarks>
/// The only substitution made anywhere in these tests. Everything upstream of it — the
/// interceptor, PostgreSQL, the outbox loop, RabbitMQ, the consumer — is the real thing;
/// this exists solely because a test needs somewhere observable for the pipeline to end.
/// </remarks>
public sealed class RecordingOrderEventHandler : IOrderEventHandler
{
    private readonly ConcurrentQueue<OrderEventNotification> _received = new();

    /// <summary>Everything received so far, in arrival order.</summary>
    public IReadOnlyCollection<OrderEventNotification> Received => _received;

    /// <inheritdoc />
    public Task HandleAsync(OrderEventNotification notification, CancellationToken cancellationToken)
    {
        _received.Enqueue(notification);
        return Task.CompletedTask;
    }

    /// <summary>Waits for a notification matching <paramref name="predicate"/> to arrive.</summary>
    /// <param name="predicate">Identifies the notification being waited for.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <param name="timeout">How long to wait before giving up.</param>
    /// <returns>The first matching notification.</returns>
    /// <exception cref="TimeoutException">Nothing matched in time.</exception>
    /// <remarks>
    /// Polling rather than a signal, because the test must be able to match on content and a
    /// signaling primitive would hand over whichever notification arrived first. The failure
    /// message lists what did arrive — a bare "timed out" tells you nothing about whether the
    /// pipeline is stalled or merely delivered something unexpected.
    /// </remarks>
    public async Task<OrderEventNotification> WaitForAsync(
        Func<OrderEventNotification, bool> predicate,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var window = timeout ?? TimeSpan.FromSeconds(15);
        var deadline = DateTimeOffset.UtcNow + window;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var match = _received.FirstOrDefault(predicate);

            if (match is not null)
            {
                return match;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        var seen = _received.Count == 0
            ? "nothing at all"
            : string.Join(", ", _received.Select(n => $"{n.OrderNumber}:{n.Status}"));

        throw new TimeoutException($"No matching notification within {window}. Received {seen}.");
    }

    /// <summary>Counts the notifications received for one order.</summary>
    /// <param name="orderNumber">The order to count for.</param>
    /// <returns>How many notifications arrived.</returns>
    public int CountFor(string orderNumber) =>
        _received.Count(notification => notification.OrderNumber == orderNumber);
}

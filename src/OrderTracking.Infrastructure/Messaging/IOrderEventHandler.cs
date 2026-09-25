namespace OrderTracking.Infrastructure.Messaging;

/// <summary>
/// Does something useful with an order event taken off the queue.
/// </summary>
/// <remarks>
/// <para>
/// At runtime, the consumer runs inside the API process, not a worker, because the
/// WebSocket connections are held in that process's memory.
/// At compile time, the consumer belongs in Infrastructure, which does not reference the API.
/// </para>
/// <para>
/// Implementations must tolerate being called more than once for the same
/// <see cref="OrderEventNotification.EventId"/>.
/// </para>
/// </remarks>
public interface IOrderEventHandler
{
    /// <summary>Handles one event.</summary>
    /// <param name="notification">What happened.</param>
    /// <param name="cancellationToken">Cancels the handling.</param>
    /// <returns>A task that completes when the event has been handled.</returns>
    Task HandleAsync(OrderEventNotification notification, CancellationToken cancellationToken);
}

namespace OrderTracking.Domain.Orders.Events;

/// <summary>
/// Raised when an order moves from one status to another.
/// </summary>
/// <remarks>
/// This is the event the specification asks to be published to the broker, and the one
/// that ultimately reaches the browser over the WebSocket.
/// </remarks>
/// <param name="EventId">Stable identity of this occurrence, used as an idempotency key.</param>
/// <param name="OccurredAt">When the status changed.</param>
/// <param name="OrderNumber">The order's public identifier.</param>
/// <param name="OldStatus">The status the order was in before the change.</param>
/// <param name="NewStatus">The status the order is in after the change.</param>
public sealed record OrderStatusChangedEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string OrderNumber,
    OrderStatus OldStatus,
    OrderStatus NewStatus) : IDomainEvent;

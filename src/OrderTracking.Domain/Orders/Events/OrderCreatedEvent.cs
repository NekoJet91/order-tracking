namespace OrderTracking.Domain.Orders.Events;

/// <summary>
/// Raised when a new order has been placed.
/// </summary>
/// <param name="EventId">Stable identity of this occurrence, used as an idempotency key.</param>
/// <param name="OccurredAt">When the order was created.</param>
/// <param name="OrderNumber">The order's public identifier.</param>
/// <param name="Description">The order description as supplied by the caller.</param>
/// <param name="Status">The status the order started in.</param>
public sealed record OrderCreatedEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string OrderNumber,
    string Description,
    OrderStatus Status) : IDomainEvent;

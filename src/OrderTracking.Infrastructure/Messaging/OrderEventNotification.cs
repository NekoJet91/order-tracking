using OrderTracking.Domain.Orders;

namespace OrderTracking.Infrastructure.Messaging;

/// <summary>
/// An order event, flattened into the shape a subscriber needs.
/// </summary>
/// <param name="EventId">Identity of the occurrence, unique per event.</param>
/// <param name="OrderNumber">The order the event is about.</param>
/// <param name="Status">The order's status after the event.</param>
/// <param name="PreviousStatus">
/// The status before the event, or <c>null</c> when the order was just created.
/// </param>
/// <param name="OccurredAt">When the event happened.</param>
/// <remarks>
/// One shape for both event types on purpose. A subscriber that only wants to refresh what
/// is on screen cares about the same four facts either way, and making it switch on an
/// event type to find them would push the branching out to every client.
/// </remarks>
public sealed record OrderEventNotification(
    Guid EventId,
    string OrderNumber,
    OrderStatus Status,
    OrderStatus? PreviousStatus,
    DateTimeOffset OccurredAt);

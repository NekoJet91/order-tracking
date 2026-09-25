namespace OrderTracking.Domain.Orders.Events;

/// <summary>
/// Something that happened in the domain and that other parts of the system may need
/// to react to.
/// </summary>
/// <remarks>
/// Events are recorded on the aggregate as it changes and are converted into outbox rows
/// inside the same database transaction as the change itself.
/// </remarks>
public interface IDomainEvent
{
    /// <summary>
    /// Stable identity of this specific occurrence.
    /// </summary>
    /// <remarks>
    /// Consumers use this as an idempotency key. The outbox guarantees at-least-once delivery
    /// </remarks>
    Guid EventId { get; }

    /// <summary>When the event occurred.</summary>
    DateTimeOffset OccurredAt { get; }
}

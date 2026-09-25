namespace OrderTracking.Domain.Orders.Events;

/// <summary>
/// An aggregate that records what happened to it.
/// </summary>
/// <remarks>
/// Exists so the persistence layer can collect events without naming a single aggregate
/// type. Without it the outbox mechanism would have to know about <see cref="Order"/>
/// specifically, and adding a second aggregate would mean editing infrastructure code
/// rather than just implementing an interface.
/// </remarks>
public interface IHasDomainEvents
{
    /// <summary>Events recorded since this instance was loaded or created.</summary>
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    /// <summary>Discards the recorded events once they have been turned into outbox rows.</summary>
    void ClearDomainEvents();
}

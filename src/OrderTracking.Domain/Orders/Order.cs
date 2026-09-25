using OrderTracking.Domain.Orders.Events;

namespace OrderTracking.Domain.Orders;

/// <summary>
/// A customer order and its position in the delivery lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// State changes go through <see cref="Create"/> and <see cref="ChangeStatus"/> rather
/// than through public setters, so the state machine cannot be bypassed and every change
/// that matters records a domain event.
/// </para>
/// <para>
/// <see cref="Id"/> is an internal surrogate key, assigned by the database during insert and
/// therefore unknown while the creation event is being recorded. <see cref="OrderNumber"/> is
/// the public identifier used in URLs, in events and on the socket; it is allocated before the
/// insert, which is what makes it safe to put in an event written in the same transaction.
/// </para>
/// </remarks>
public sealed class Order : IHasDomainEvents
{
    /// <summary>Maximum permitted length of <see cref="OrderNumber"/>.</summary>
    public const int OrderNumberMaxLength = 32;

    /// <summary>Maximum permitted length of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 1000;

    private readonly List<IDomainEvent> _domainEvents = [];

    private Order()
    {
        // Used by EF Core when materialising a row.
        OrderNumber = null!;
        Description = null!;
    }

    private Order(string orderNumber, string description, DateTimeOffset now)
    {
        OrderNumber = orderNumber;
        Description = description;
        Status = OrderStatus.Created;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>Surrogate primary key. Internal to persistence; not exposed by the API.</summary>
    public long Id { get; private set; }

    /// <summary>
    /// Human-readable unique identifier, for example <c>ORD-00000042</c>. This is the
    /// identifier the API routes on and that events carry.
    /// </summary>
    public string OrderNumber { get; private set; }

    /// <summary>Free-text description of what was ordered.</summary>
    public string Description { get; private set; }

    /// <summary>Where the order currently sits in its lifecycle.</summary>
    public OrderStatus Status { get; private set; }

    /// <summary>When the order was placed.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// When the order last changed. Equal to <see cref="CreatedAt"/> until the first
    /// status change.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Events recorded since this instance was loaded or created, awaiting conversion into outbox rows.
    /// </summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents;

    /// <summary>
    /// Places a new order in the <see cref="OrderStatus.Created"/> state.
    /// </summary>
    /// <param name="orderNumber">
    /// The allocated public identifier. Must already be unique — uniqueness is enforced
    /// by the database, not here.
    /// </param>
    /// <param name="description">What was ordered.</param>
    /// <param name="now">
    /// The current time, supplied by the caller rather than read from the clock so that
    /// the domain stays deterministic and testable.
    /// </param>
    /// <returns>The new order, carrying an <see cref="OrderCreatedEvent"/>.</returns>
    /// <exception cref="ArgumentException">
    /// A required value is missing, blank, or longer than permitted.
    /// </exception>
    public static Order Create(string orderNumber, string description, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        if (orderNumber.Length > OrderNumberMaxLength)
        {
            throw new ArgumentException(
                $"Order number must be at most {OrderNumberMaxLength} characters.", nameof(orderNumber));
        }

        if (description.Length > DescriptionMaxLength)
        {
            throw new ArgumentException(
                $"Description must be at most {DescriptionMaxLength} characters.", nameof(description));
        }

        var order = new Order(orderNumber, description, now);

        order._domainEvents.Add(new OrderCreatedEvent(
            EventId: Guid.NewGuid(),
            OccurredAt: now,
            OrderNumber: order.OrderNumber,
            Description: order.Description,
            Status: order.Status));

        return order;
    }

    /// <summary>
    /// Moves the order to <paramref name="newStatus"/> if the state machine permits it.
    /// </summary>
    /// <param name="newStatus">The requested status.</param>
    /// <param name="now">The current time, supplied by the caller.</param>
    /// <returns>
    /// <c>true</c> if the order changed and an <see cref="OrderStatusChangedEvent"/> was
    /// recorded; <c>false</c> if the order was already in <paramref name="newStatus"/> and
    /// nothing happened.
    /// </returns>
    /// <remarks>
    /// Requesting the status the order is already in is deliberately a no-op rather than an
    /// error, so that a client retrying after a timeout succeeds instead of receiving a
    /// spurious conflict, and so that no duplicate event is published.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="newStatus"/> is not a defined <see cref="OrderStatus"/>.
    /// </exception>
    /// <exception cref="InvalidOrderStatusTransitionException">
    /// The transition is defined but not permitted from the current status.
    /// </exception>
    public bool ChangeStatus(OrderStatus newStatus, DateTimeOffset now)
    {
        if (!Enum.IsDefined(newStatus))
        {
            throw new ArgumentOutOfRangeException(
                nameof(newStatus), newStatus, "Not a defined order status.");
        }

        if (newStatus == Status)
        {
            return false;
        }

        if (!OrderStatusTransitions.IsAllowed(Status, newStatus))
        {
            throw new InvalidOrderStatusTransitionException(Status, newStatus);
        }

        var previousStatus = Status;
        Status = newStatus;
        UpdatedAt = now;

        _domainEvents.Add(new OrderStatusChangedEvent(
            EventId: Guid.NewGuid(),
            OccurredAt: now,
            OrderNumber: OrderNumber,
            OldStatus: previousStatus,
            NewStatus: newStatus));

        return true;
    }

    /// <summary>
    /// Discards the recorded events. Called once they have been turned into outbox rows.
    /// </summary>
    public void ClearDomainEvents() => _domainEvents.Clear();
}

namespace OrderTracking.Domain.Orders;

/// <summary>
/// The order status state machine: which status changes are permitted.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="Order"/> so the rules can be enumerated and tested
/// exhaustively, and so the API can advertise the legal next steps for a given order.
/// </remarks>
public static class OrderStatusTransitions
{
    private static readonly IReadOnlyDictionary<OrderStatus, IReadOnlySet<OrderStatus>> _allowedTransitions =
        new Dictionary<OrderStatus, IReadOnlySet<OrderStatus>>
        {
            [OrderStatus.Created] = new HashSet<OrderStatus> { OrderStatus.Shipped, OrderStatus.Cancelled },
            [OrderStatus.Shipped] = new HashSet<OrderStatus> { OrderStatus.Delivered, OrderStatus.Cancelled },
            [OrderStatus.Delivered] = new HashSet<OrderStatus>(),
            [OrderStatus.Cancelled] = new HashSet<OrderStatus>()
        };

    /// <summary>
    /// The statuses reachable in a single step from <paramref name="from"/>.
    /// Empty for terminal statuses.
    /// </summary>
    /// <param name="from">The current status.</param>
    /// <returns>The set of permitted next statuses.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="from"/> is not a defined <see cref="OrderStatus"/>.
    /// </exception>
    public static IReadOnlySet<OrderStatus> AllowedFrom(OrderStatus from) =>
        _allowedTransitions.TryGetValue(from, out var next)
            ? next
            : throw new ArgumentOutOfRangeException(nameof(from), from, "Not a defined order status.");

    /// <summary>
    /// Whether moving from <paramref name="from"/> to <paramref name="to"/> is permitted.
    /// </summary>
    /// <param name="from">The current status.</param>
    /// <param name="to">The requested status.</param>
    /// <returns><c>true</c> if the transition is legal.</returns>
    /// <remarks>
    /// A transition to the same status returns <c>false</c>.
    /// </remarks>
    public static bool IsAllowed(OrderStatus from, OrderStatus to) => AllowedFrom(from).Contains(to);

    /// <summary>
    /// Whether no further transitions are possible from <paramref name="status"/>.
    /// </summary>
    /// <param name="status">The status to inspect.</param>
    /// <returns><c>true</c> if the status is terminal.</returns>
    public static bool IsTerminal(OrderStatus status) => AllowedFrom(status).Count == 0;
}

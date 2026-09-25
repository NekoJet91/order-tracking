namespace OrderTracking.Domain.Orders;

/// <summary>
/// Thrown when a status change is requested that the state machine does not permit.
/// </summary>
/// <remarks>
/// The API maps this to <c>409 Conflict</c> rather than <c>400 Bad Request</c>: the request
/// was well formed and the requested status is a real one, so nothing about the message is
/// wrong. It was refused because of the order's current state, and the same request would
/// have succeeded a moment earlier — which is exactly what <c>409</c> means.
/// </remarks>
public sealed class InvalidOrderStatusTransitionException : InvalidOperationException
{
    /// <summary>
    /// Initializes the exception for a specific rejected transition.
    /// </summary>
    /// <param name="from">The order's current status.</param>
    /// <param name="to">The status that was requested.</param>
    public InvalidOrderStatusTransitionException(OrderStatus from, OrderStatus to)
        : base($"Cannot change order status from '{from}' to '{to}'.")
    {
        From = from;
        To = to;
    }

    /// <summary>The order's status at the time the change was rejected.</summary>
    public OrderStatus From { get; }

    /// <summary>The status that was requested.</summary>
    public OrderStatus To { get; }
}

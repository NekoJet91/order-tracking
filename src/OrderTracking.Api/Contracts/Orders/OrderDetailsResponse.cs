using OrderTracking.Domain.Orders;

namespace OrderTracking.Api.Contracts.Orders;

/// <summary>
/// A single order, with everything a details page needs.
/// </summary>
/// <param name="OrderNumber">The order's public identifier, for example <c>ORD-00000042</c>.</param>
/// <param name="Description">What was ordered.</param>
/// <param name="Status">Where the order currently sits in its lifecycle.</param>
/// <param name="AllowedNextStatuses">
/// The statuses this order can move to in one step. Empty once the order reaches a
/// terminal status.
/// </param>
/// <param name="CreatedAt">When the order was placed.</param>
/// <param name="UpdatedAt">When the order last changed status.</param>
/// <remarks>
/// <paramref name="AllowedNextStatuses"/> exists so the client does not have to reimplement
/// the state machine to decide which buttons to enable.
/// </remarks>
public sealed record OrderDetailsResponse(
    string OrderNumber,
    string Description,
    OrderStatus Status,
    IReadOnlyList<OrderStatus> AllowedNextStatuses,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

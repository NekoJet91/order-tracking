using OrderTracking.Domain.Orders;

namespace OrderTracking.Api.Contracts.Orders;

/// <summary>
/// An order as it appears in a list.
/// </summary>
/// <param name="OrderNumber">The order's public identifier, for example <c>ORD-00000042</c>.</param>
/// <param name="Description">What was ordered.</param>
/// <param name="Status">Where the order currently sits in its lifecycle.</param>
/// <param name="CreatedAt">When the order was placed.</param>
/// <param name="UpdatedAt">When the order last changed status.</param>
public sealed record OrderSummaryResponse(
    string OrderNumber,
    string Description,
    OrderStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

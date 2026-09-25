using OrderTracking.Domain.Orders;

namespace OrderTracking.Api.Contracts.Orders;

/// <summary>
/// Body of a request to move an order to a different status.
/// </summary>
/// <param name="Status">
/// The status to move the order to. Must be reachable in one step from the order's
/// current status.
/// </param>
public sealed record ChangeOrderStatusRequest(OrderStatus Status);

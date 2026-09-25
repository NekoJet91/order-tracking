namespace OrderTracking.Api.Contracts.Orders;

/// <summary>
/// Body of a request to place a new order.
/// </summary>
/// <param name="Description">
/// What is being ordered. Required, and at most 1000 characters. Surrounding whitespace is
/// trimmed before the order is stored.
/// </param>
/// <remarks>
/// The order number is allocated by the server, so the client does not supply one.
/// </remarks>
public sealed record CreateOrderRequest(string Description);

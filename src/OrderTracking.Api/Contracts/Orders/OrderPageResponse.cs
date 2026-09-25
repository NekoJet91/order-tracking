namespace OrderTracking.Api.Contracts.Orders;

/// <summary>
/// One page of orders, newest first.
/// </summary>
/// <param name="Items">The orders on this page.</param>
/// <param name="NextCursor">
/// Opaque token to pass as <c>cursor</c> to fetch the following page, or <c>null</c> when
/// this is the last page.
/// </param>
/// <remarks>
/// There is deliberately no total count. Counting the whole table on every page request is
/// the expensive half of offset pagination, and the UI here does not need it.
/// </remarks>
public sealed record OrderPageResponse(
    IReadOnlyList<OrderSummaryResponse> Items,
    string? NextCursor);

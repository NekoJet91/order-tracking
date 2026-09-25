using OrderTracking.Api.Contracts.Orders;
using OrderTracking.Domain.Orders;

namespace OrderTracking.Api.Features.Orders;

/// <summary>
/// Query shapes shared by the REST endpoints and the socket.
/// </summary>
public static class OrderQueries
{
    /// <summary>
    /// Projects orders into <see cref="OrderSummaryResponse"/> on the database side.
    /// </summary>
    /// <param name="orders">The query to project.</param>
    /// <returns>A query that selects only the summary columns and tracks nothing.</returns>
    /// <remarks>
    /// One definition for the list endpoint, the snapshot and the broadcast frame, so the
    /// three cannot drift apart. Projecting into a non-entity type already means no change
    /// tracking, so callers do not need <c>AsNoTracking</c> in front of this.
    /// </remarks>
    public static IQueryable<OrderSummaryResponse> SelectSummary(this IQueryable<Order> orders) =>
        orders.Select(order => new OrderSummaryResponse(
            order.OrderNumber,
            order.Description,
            order.Status,
            order.CreatedAt,
            order.UpdatedAt));
}

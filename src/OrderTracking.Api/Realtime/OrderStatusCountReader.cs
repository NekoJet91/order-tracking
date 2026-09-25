using Microsoft.EntityFrameworkCore;
using OrderTracking.Domain.Orders;
using OrderTracking.Infrastructure.Persistence;

namespace OrderTracking.Api.Realtime;

/// <summary>
/// Counts how many orders sit in each status.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by the status name rather than modelled as a record with one property per status,
/// so the result is complete by construction: it is built from <see cref="Enum.GetValues{T}"/>
/// and a status added tomorrow appears with a zero rather than being silently omitted.
/// </para>
/// <para>
/// This is an aggregate over the whole table and it runs once per connect and once per
/// change. That cost is accepted in exchange for exactness: every change emits a frame, so a
/// total sent this way is never stale. At a volume where the scan mattered, the replacement
/// is a counter row maintained in the same transaction as the status change.
/// </para>
/// </remarks>
internal static class OrderStatusCountReader
{
    /// <summary>Reads the current per-status totals.</summary>
    /// <param name="dbContext">The context to query.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Every status, including those with no orders.</returns>
    public static async Task<IReadOnlyDictionary<string, int>> ReadAsync(
        OrderTrackingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var counted = await dbContext.Orders
            .AsNoTracking()
            .GroupBy(order => order.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Status, row => row.Count, cancellationToken)
            .ConfigureAwait(false);

        return Enum.GetValues<OrderStatus>()
            .ToDictionary(status => status.ToString(), counted.GetValueOrDefault);
    }

    /// <summary>Reads the current per-status totals in a scope of its own.</summary>
    /// <param name="scopeFactory">Supplies the scope the context is resolved from.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Every status, including those with no orders.</returns>
    public static async Task<IReadOnlyDictionary<string, int>> ReadAsync(
        IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();

        return await ReadAsync(dbContext, cancellationToken).ConfigureAwait(false);
    }
}

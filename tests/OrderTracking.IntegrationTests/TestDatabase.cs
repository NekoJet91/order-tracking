using Microsoft.EntityFrameworkCore;
using OrderTracking.Infrastructure.Persistence;

namespace OrderTracking.IntegrationTests;

/// <summary>
/// Puts a freshly created database into the state a test class expects.
/// </summary>
internal static class TestDatabase
{
    /// <summary>Brings the schema up to date and empties it.</summary>
    /// <remarks>
    /// Safe to call repeatedly: the promise is an empty schema, whatever state it was in.
    /// RESTART IDENTITY resets the tables' identity columns but not the separate
    /// order_number_seq, so tests must not assume a particular first order number.
    /// </remarks>
    public static async Task ResetAsync(OrderTrackingDbContext dbContext, CancellationToken cancellationToken)
    {
        await dbContext.Database.MigrateAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "TRUNCATE orders, outbox_messages, processed_messages RESTART IDENTITY", cancellationToken);
    }
}

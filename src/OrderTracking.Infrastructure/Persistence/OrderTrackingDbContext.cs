using Microsoft.EntityFrameworkCore;
using OrderTracking.Domain.Orders;
using OrderTracking.Infrastructure.Messaging;
using OrderTracking.Infrastructure.Outbox;

namespace OrderTracking.Infrastructure.Persistence;

/// <summary>
/// The application's EF Core context: orders and the transactional outbox.
/// </summary>
/// <param name="options">Provider and connection configuration.</param>
public sealed class OrderTrackingDbContext(DbContextOptions<OrderTrackingDbContext> options)
    : DbContext(options)
{
    /// <summary>
    /// Name of the PostgreSQL sequence backing <see cref="Order.OrderNumber"/>.
    /// </summary>
    public const string OrderNumberSequenceName = "order_number_seq";

    /// <summary>Orders being tracked.</summary>
    public DbSet<Order> Orders => Set<Order>();

    /// <summary>Events awaiting publication to the broker.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>Broker messages already handled, used to discard redeliveries.</summary>
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configuration lives in one class per entity rather than inline here
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrderTrackingDbContext).Assembly);

        modelBuilder.HasSequence<long>(OrderNumberSequenceName)
                    .StartsAt(1)
                    .IncrementsBy(1);
    }
}

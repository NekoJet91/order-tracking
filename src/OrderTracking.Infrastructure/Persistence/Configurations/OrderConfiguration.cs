using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderTracking.Domain.Orders;

namespace OrderTracking.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Order"/> to the <c>orders</c> table.
/// </summary>
internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).UseIdentityAlwaysColumn();

        builder.Property(o => o.OrderNumber)
               .HasMaxLength(Order.OrderNumberMaxLength)
               .IsRequired();

        builder.HasIndex(o => o.OrderNumber)
               .IsUnique();

        builder.Property(o => o.Description)
               .HasMaxLength(Order.DescriptionMaxLength)
               .IsRequired();

        // Stored as text, not as the underlying integers. The column is then readable in
        // any SQL client, and reordering or renumbering the enum cannot silently
        // reinterpret existing rows.
        builder.Property(o => o.Status)
               .HasConversion<string>()
               .HasMaxLength(16)
               .IsRequired();

        builder.Property(o => o.CreatedAt).IsRequired();
        builder.Property(o => o.UpdatedAt).IsRequired();

        // Supports the list endpoint's keyset pagination, which orders by CreatedAt then
        // Id descending. Id is included to break ties deterministically: two orders
        // created in the same instant would otherwise be able to swap places between
        // pages, skipping one and repeating the other.
        builder.HasIndex(o => new { o.CreatedAt, o.Id })
               .IsDescending()
               .HasDatabaseName("ix_orders_created_at_id_desc");

        // Optimistic concurrency against PostgreSQL's xmin system column, which already
        // holds the id of the transaction that last wrote the row — so two simultaneous
        // status changes cannot silently overwrite one another, at the cost of no extra
        // column and no extra write. Declared as a shadow property so the domain entity
        // carries no persistence concern.
        //
        // (Npgsql's UseXminAsConcurrencyToken() did exactly this and is obsolete in 8.0;
        // the standard IsRowVersion() on a uint mapped to "xmin" is the replacement.)
        builder.Property<uint>("xmin")
               .HasColumnName("xmin")
               .IsRowVersion();

        builder.Ignore(o => o.DomainEvents);
    }
}

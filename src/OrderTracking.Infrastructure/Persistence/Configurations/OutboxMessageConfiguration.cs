using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderTracking.Infrastructure.Outbox;

namespace OrderTracking.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="OutboxMessage"/> to the <c>outbox_messages</c> table.
/// </summary>
internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).UseIdentityAlwaysColumn();

        builder.Property(m => m.MessageId).IsRequired();
        builder.HasIndex(m => m.MessageId).IsUnique();

        builder.Property(m => m.Type).HasMaxLength(200).IsRequired();

        builder.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();

        builder.Property(m => m.OccurredAt).IsRequired();
        builder.Property(m => m.AttemptCount).IsRequired();
        builder.Property(m => m.TraceParent).HasMaxLength(64);

        // Partial index: the publisher only ever asks for pending rows, and once a message
        // is published its row becomes dead weight in the index.
        builder.HasIndex(m => m.Id)
               .HasFilter("processed_at IS NULL")
               .HasDatabaseName("ix_outbox_messages_pending");
    }
}

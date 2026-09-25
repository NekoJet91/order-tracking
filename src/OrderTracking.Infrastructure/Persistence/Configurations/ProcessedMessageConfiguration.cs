using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderTracking.Infrastructure.Messaging;

namespace OrderTracking.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ProcessedMessage"/> to the <c>processed_messages</c> table.
/// </summary>
internal sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        builder.ToTable("processed_messages");

        // The event id is the key outright, with no surrogate. The uniqueness constraint is
        // the deduplication mechanism, so it should be the thing the table is built around
        // rather than an index sitting beside an identity column that means nothing.
        builder.HasKey(message => message.MessageId);

        builder.Property(message => message.ProcessedAt).IsRequired();
    }
}

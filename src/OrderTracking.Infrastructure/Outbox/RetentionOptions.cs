namespace OrderTracking.Infrastructure.Outbox;

/// <summary>
/// How long the two bookkeeping tables keep rows that have served their purpose.
/// </summary>
/// <remarks>
/// Both tables grow with throughput and neither is read once its row has done its job, so
/// without a sweep they are the part of this system that fails first — not by breaking, but
/// by making every query against them slower for years.
/// </remarks>
public sealed class RetentionOptions
{
    /// <summary>Configuration section these settings are bound from.</summary>
    public const string SectionName = "Retention";

    /// <summary>Set to <c>false</c> to keep everything.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often to sweep.</summary>
    /// <remarks>
    /// Hourly rather than continuously. Deleting is not urgent, and a sweep that runs rarely
    /// is a sweep whose locks are not competing with the publisher's.
    /// </remarks>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long a published outbox row is kept.</summary>
    /// <remarks>
    /// Long enough to answer "was this event ever sent?" during an incident. Rows that were
    /// never published are never deleted, whatever their age — an event the system promised
    /// to deliver and did not is evidence, not clutter.
    /// </remarks>
    public TimeSpan OutboxPeriod { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How long a deduplication marker is kept.</summary>
    /// <remarks>
    /// This one is a correctness setting, not housekeeping. A marker is what makes a
    /// redelivery a no-op, so deleting one while the broker could still redeliver its message
    /// turns at-least-once back into actually-twice. It must exceed the longest redelivery
    /// the broker can produce, which in practice means longer than the queue can be down.
    /// </remarks>
    public TimeSpan ProcessedMessagePeriod { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Rows deleted per statement.</summary>
    /// <remarks>
    /// Chunked so a first sweep against a table that has grown for months does not take a
    /// lock on a million rows in one transaction.
    /// </remarks>
    public int BatchSize { get; set; } = 1_000;
}

namespace OrderTracking.Infrastructure.Outbox;

/// <summary>
/// Tuning for the background loop that drains the outbox.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>Configuration section these settings are bound from.</summary>
    public const string SectionName = "Outbox";

    /// <summary>
    /// How long to wait before looking for work again when the last sweep found nothing.
    /// </summary>
    /// <remarks>
    /// This is the floor on notification latency, which is why it is short.
    /// </remarks>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum rows claimed per sweep.</summary>
    /// <remarks>
    /// Bounded because the rows stay locked while they are published, and publishing is
    /// network I/O. A large batch would hold locks for a long time and delay every other
    /// instance of the publisher.
    /// </remarks>
    public int BatchSize { get; set; } = 50;

    /// <summary>How many times a message may fail to publish before it is left alone.</summary>
    /// <remarks>
    /// Exhausted rows are not deleted. They stay pending with their last error recorded, so
    /// a human can see them; silently dropping an event the system promised to deliver is
    /// worse than a queue that visibly stops.
    /// </remarks>
    public int MaxAttempts { get; set; } = 5;
}

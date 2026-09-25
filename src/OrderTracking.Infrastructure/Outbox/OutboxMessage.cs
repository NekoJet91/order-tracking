namespace OrderTracking.Infrastructure.Outbox;

/// <summary>
/// A domain event persisted for later publication to the broker.
/// </summary>
/// <remarks>
/// <para>
/// Rows are written in the same transaction as the change that produced them.
/// </para>
/// </remarks>
public sealed class OutboxMessage
{
    /// <summary>Surrogate key, also the natural publication order.</summary>
    public long Id { get; set; }

    /// <summary>
    /// Identity of the underlying domain event, carried through to the broker message so
    /// consumers can discard duplicates.
    /// </summary>
    public Guid MessageId { get; set; }

    /// <summary>Event type name, used as the routing key and to pick a deserializer.</summary>
    public string Type { get; set; } = null!;

    /// <summary>The serialized event body, stored as <c>jsonb</c>.</summary>
    public string Payload { get; set; } = null!;

    /// <summary>When the originating domain event occurred.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>
    /// When the broker confirmed the message, or <c>null</c> while it is still pending.
    /// </summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>How many publication attempts have been made.</summary>
    public int AttemptCount { get; set; }

    /// <summary>The failure from the most recent attempt, if any.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// W3C <c>traceparent</c> captured when the event was recorded.
    /// </summary>
    /// <remarks>
    /// Replayed onto the broker message so a trace can span the HTTP request, the
    /// publication and the consumer, rather than breaking at the queue boundary.
    /// </remarks>
    public string? TraceParent { get; set; }
}

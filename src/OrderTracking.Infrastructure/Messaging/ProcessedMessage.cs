namespace OrderTracking.Infrastructure.Messaging;

/// <summary>
/// Record that a broker message has already been handled.
/// </summary>
/// <remarks>
/// <para>
/// The outbox guarantees at-least-once delivery.
/// </para>
/// </remarks>
public sealed class ProcessedMessage
{
    /// <summary>The event id carried by the message.</summary>
    public Guid MessageId { get; set; }

    /// <summary>When the message was handled.</summary>
    public DateTimeOffset ProcessedAt { get; set; }
}

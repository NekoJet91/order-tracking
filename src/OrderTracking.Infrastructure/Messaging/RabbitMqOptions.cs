namespace OrderTracking.Infrastructure.Messaging;

/// <summary>
/// Connection and topology settings for RabbitMQ.
/// </summary>
public sealed class RabbitMqOptions
{
    /// <summary>Configuration section these settings are bound from.</summary>
    public const string SectionName = "RabbitMq";

    /// <summary>Broker host name. <c>rabbitmq</c> under Compose, <c>localhost</c> otherwise.</summary>
    public string HostName { get; set; } = "localhost";

    /// <summary>AMQP port.</summary>
    public int Port { get; set; } = 5672;

    /// <summary>User to authenticate as.</summary>
    public string UserName { get; set; } = "guest";

    /// <summary>Password for <see cref="UserName"/>.</summary>
    public string Password { get; set; } = "guest";

    /// <summary>Virtual host to connect to.</summary>
    public string VirtualHost { get; set; } = "/";

    /// <summary>Topic exchange order events are published to.</summary>
    public string Exchange { get; set; } = "order-tracking";

    /// <summary>Queue the notification consumer reads from.</summary>
    public string Queue { get; set; } = "order-tracking.notifications";

    /// <summary>
    /// Messages the consumer rejects are routed here.
    /// </summary>
    /// <remarks>
    /// A dead-letter exchange rather than requeueing
    /// </remarks>
    public string DeadLetterExchange { get; set; } = "order-tracking.dlx";

    /// <summary>Queue bound to <see cref="DeadLetterExchange"/>.</summary>
    public string DeadLetterQueue { get; set; } = "order-tracking.notifications.dlq";

    /// <summary>
    /// How many unacknowledged messages the broker may have in flight per consumer.
    /// </summary>
    /// <remarks>
    /// Without this the broker pushes the entire queue at once, the consumer buffers all of
    /// it in memory, and nothing is left for a second instance to pick up.
    /// </remarks>
    public ushort PrefetchCount { get; set; } = 16;
}

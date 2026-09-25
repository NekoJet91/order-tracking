using OrderTracking.Domain.Orders.Events;
using RabbitMQ.Client;

namespace OrderTracking.Infrastructure.Messaging;

/// <summary>
/// Declares the exchanges, queues and bindings the application relies on.
/// </summary>
/// <remarks>
/// <para>
/// Declared by both the publisher and the consumer, on every start. AMQP declarations are
/// idempotent, so this is not wasteful — it is what lets either side start first, and what
/// lets the system come up against an empty broker with no manual setup step. That matters
/// for <c>docker compose up</c> from a clean clone.
/// </para>
/// <para>
/// Everything is durable and messages are persistent, so a broker restart does not lose
/// events that the database already committed. Durability that stops at the exchange would
/// defeat the point of having written an outbox row at all.
/// </para>
/// </remarks>
internal static class RabbitMqTopology
{
    /// <summary>Routing key prefix shared by every order event.</summary>
    public const string RoutingKeyPrefix = "order";

    /// <summary>Binding pattern matching every order event.</summary>
    public const string BindingPattern = "order.#";

    /// <summary>Declares everything, idempotently.</summary>
    /// <param name="channel">Channel to declare on.</param>
    /// <param name="options">Names to declare.</param>
    /// <param name="cancellationToken">Cancels the declarations.</param>
    public static async Task DeclareAsync(
        IChannel channel,
        RabbitMqOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(options);

        // Topic rather than direct, so a future consumer can subscribe to a slice — say
        // order.status-changed only — without the publisher being changed or even told.
        await channel.ExchangeDeclareAsync(
            exchange: options.Exchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(
            exchange: options.DeadLetterExchange,
            type: ExchangeType.Fanout,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueDeclareAsync(
            queue: options.DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueBindAsync(
            queue: options.DeadLetterQueue,
            exchange: options.DeadLetterExchange,
            routingKey: string.Empty,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueDeclareAsync(
            queue: options.Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = options.DeadLetterExchange
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueBindAsync(
            queue: options.Queue,
            exchange: options.Exchange,
            routingKey: BindingPattern,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps a domain event type name to its routing key.
    /// </summary>
    /// <param name="eventType">The event type name stored on the outbox row.</param>
    /// <returns>A dotted, lower-case routing key under <see cref="RoutingKeyPrefix"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="eventType"/> is not an event this topology knows how to route.
    /// </exception>
    /// <remarks>
    /// An explicit table rather than a derivation from the type name. The consumer's
    /// deserializer is a matching table, so a new event type has to be added in both places
    /// anyway — and a name that is not in the table is a bug, which should fail here rather
    /// than be published under a key nothing is bound to.
    /// </remarks>
    public static string RoutingKeyFor(string eventType) => eventType switch
    {
        nameof(OrderCreatedEvent) => $"{RoutingKeyPrefix}.created",
        nameof(OrderStatusChangedEvent) => $"{RoutingKeyPrefix}.status-changed",
        _ => throw new ArgumentOutOfRangeException(nameof(eventType), eventType, "Not a routable event type.")
    };
}

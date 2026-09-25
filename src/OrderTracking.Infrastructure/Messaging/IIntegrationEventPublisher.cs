using OrderTracking.Infrastructure.Outbox;

namespace OrderTracking.Infrastructure.Messaging;

/// <summary>
/// Sends an outbox row to the broker.
/// </summary>
/// <remarks>
/// <para>
/// Separates "drain the table reliably" from "talk to RabbitMQ". The two share no
/// vocabulary — one is locking, retries and ordering, the other connections, channels and
/// publisher confirms — so pulling them apart means each side changes for one reason.
/// </para>
/// <para>
/// There is one implementation and no test substitutes a fake for it. What the interface buys
/// today is a compiler-enforced ceiling on how much <c>OutboxPublisher</c> can learn about the
/// broker, which against a concrete class would otherwise grow one reasonable-looking member
/// at a time.
/// </para>
/// </remarks>
public interface IIntegrationEventPublisher
{
    /// <summary>Publishes one message, returning once the broker has confirmed it.</summary>
    /// <param name="message">The outbox row to publish.</param>
    /// <param name="cancellationToken">Cancels the publication.</param>
    /// <returns>A task that completes when the broker has acknowledged the message.</returns>
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}

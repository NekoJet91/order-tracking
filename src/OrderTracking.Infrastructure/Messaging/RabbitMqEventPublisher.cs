using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using OrderTracking.Infrastructure.Diagnostics;
using OrderTracking.Infrastructure.Outbox;
using RabbitMQ.Client;

namespace OrderTracking.Infrastructure.Messaging;

/// <summary>
/// Publishes outbox rows to a RabbitMQ topic exchange.
/// </summary>
/// <param name="connection">The shared AMQP connection.</param>
/// <param name="options">Broker settings.</param>
/// <remarks>
/// <para>
/// Holds one of the channels on the shared connection — the consumer opens its own. They
/// stay separate deliberately: a channel-level error closes the channel, so sharing would
/// let a bad publish take the subscription down with it, and publisher confirms are a
/// per-channel mode the consumer has no use for. One channel here rather than one per
/// message, because creating a channel costs a round trip to the broker.
/// </para>
/// <para>
/// A confirmed publish does not return until the broker acknowledges, so one message is in
/// flight at a time.
/// </para>
/// </remarks>
public sealed class RabbitMqEventPublisher(
    RabbitMqConnection connection,
    IOptions<RabbitMqOptions> options) : IIntegrationEventPublisher, IAsyncDisposable
{
    private readonly RabbitMqOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    private readonly SemaphoreSlim _gate = new(1, 1);

    private IChannel? _channel;

    /// <inheritdoc />
    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var channel = await GetChannelAsync(cancellationToken).ConfigureAwait(false);
        var routingKey = RabbitMqTopology.RoutingKeyFor(message.Type);

        // Parented by the request that wrote the row, not by the sweep that found it. The
        // sweep is a loop with no meaning of its own; the request is what a reader is
        // following. Without this the trace would end at SaveChanges and a second, unrelated
        // trace would start here.
        using var activity = OrderTrackingDiagnostics.StartActivity(
            $"{routingKey} publish", ActivityKind.Producer, ParentOf(message));

        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", _options.Exchange);
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", routingKey);
        activity?.SetTag("messaging.message.id", message.MessageId);

        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            MessageId = message.MessageId.ToString(),
            Type = message.Type,
            Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds())
        };

        // The span's own id, so the consumer becomes its child rather than a sibling of the
        // original request. Falls back to the stored value when nothing is listening, which
        // keeps the chain intact even with tracing switched off.
        var traceParent = activity?.Id ?? message.TraceParent;

        if (traceParent is not null)
        {
            properties.Headers = new Dictionary<string, object?>
            {
                ["traceparent"] = traceParent
            };
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            //Doesn't return until the broker processed the message properly
            await channel.BasicPublishAsync(
                exchange: _options.Exchange,
                routingKey: routingKey,
                mandatory: true,
                basicProperties: properties,
                body: Encoding.UTF8.GetBytes(message.Payload),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ActivityContext ParentOf(OutboxMessage message) =>
        ActivityContext.TryParse(message.TraceParent, traceState: null, out var context)
            ? context
            : default;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
            _channel = null;
        }

        _gate.Dispose();
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        var amqp = await connection.GetAsync(cancellationToken).ConfigureAwait(false);

        var channel = await amqp.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            cancellationToken).ConfigureAwait(false);

        await RabbitMqTopology.DeclareAsync(channel, _options, cancellationToken).ConfigureAwait(false);

        _channel = channel;
        return channel;
    }
}

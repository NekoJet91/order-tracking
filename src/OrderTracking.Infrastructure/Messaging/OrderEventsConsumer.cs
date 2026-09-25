using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderTracking.Domain.Orders.Events;
using OrderTracking.Infrastructure.Diagnostics;
using OrderTracking.Infrastructure.Outbox;
using OrderTracking.Infrastructure.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OrderTracking.Infrastructure.Messaging;

/// <summary>
/// Consumes order events from RabbitMQ and hands them to <see cref="IOrderEventHandler"/>.
/// </summary>
/// <param name="scopeFactory">Creates a scope per message.</param>
/// <param name="connection">The shared AMQP connection.</param>
/// <param name="timeProvider">Clock used to stamp the deduplication marker.</param>
/// <param name="options">Broker settings.</param>
/// <param name="logger">Receives progress and failures.</param>
/// <remarks>
/// Runs inside the API process rather than as a separate worker, because the WebSocket
/// connections it will feed are held in this process's memory. Scaling the API to more than
/// one instance therefore needs a backplane — that is the deliberate limit of this design,
/// and it is stated in the README rather than discovered later.
/// </remarks>
public sealed partial class OrderEventsConsumer(
    IServiceScopeFactory scopeFactory,
    RabbitMqConnection connection,
    TimeProvider timeProvider,
    IOptions<RabbitMqOptions> options,
    ILogger<OrderEventsConsumer> logger) : BackgroundService
{
    private readonly RabbitMqOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var amqp = await connection.GetAsync(stoppingToken).ConfigureAwait(false);
        await using var channel = await amqp.CreateChannelAsync(cancellationToken: stoppingToken)
            .ConfigureAwait(false);

        await RabbitMqTopology.DeclareAsync(channel, _options, stoppingToken).ConfigureAwait(false);

        // Without a prefetch limit the broker pushes the whole queue at this consumer, which
        // buffers it in memory and leaves nothing for a second instance to take.
        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: _options.PrefetchCount,
            global: false,
            cancellationToken: stoppingToken).ConfigureAwait(false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => OnReceivedAsync(channel, delivery, stoppingToken);

        // autoAck: false. With it on, the broker considers a message delivered the moment it
        // leaves, so a crash mid-handling loses it silently — which would undo everything the
        // outbox was built to guarantee on the way in.
        await channel.BasicConsumeAsync(
            queue: _options.Queue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken).ConfigureAwait(false);

        LogConsuming(logger, _options.Queue, _options.PrefetchCount);

        // Nothing left to do on this thread; deliveries arrive on the client's dispatcher.
        // The delay exists only to keep the channel alive until shutdown is requested.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogStopping(logger);
        }
    }

    private async Task OnReceivedAsync(
        IChannel channel,
        BasicDeliverEventArgs delivery,
        CancellationToken stoppingToken)
    {
        // Continues the trace the publisher started, rather than beginning a new one. A
        // message is the only thread connecting the two processes, so the header it carries
        // is the only thing that can carry the context across.
        using var activity = OrderTrackingDiagnostics.StartActivity(
            $"{delivery.RoutingKey} process", ActivityKind.Consumer, ParentOf(delivery));

        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", _options.Queue);
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", delivery.RoutingKey);

        try
        {
            var notification = Parse(delivery);

            if (notification is null)
            {
                // Unparseable and will stay unparseable however many times it is retried, so
                // it goes straight to the dead-letter queue instead of round-tripping forever.
                LogUnreadable(logger, delivery.RoutingKey);
                OrderTrackingDiagnostics.MessageHandled("unreadable");
                activity?.SetStatus(ActivityStatusCode.Error, "Message could not be read.");
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false,
                    stoppingToken).ConfigureAwait(false);
                return;
            }

            // A scope per message: the handler and the DbContext are scoped, and reusing one
            // across messages would leak tracked entities from every message before it.
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();

            activity?.SetTag("messaging.message.id", notification.EventId);
            activity?.SetTag("ordertracking.order_number", notification.OrderNumber);

            if (await AlreadyHandledAsync(dbContext, notification.EventId, stoppingToken)
                    .ConfigureAwait(false))
            {
                LogDuplicate(logger, notification.EventId);
                OrderTrackingDiagnostics.MessageHandled("duplicate");
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken)
                    .ConfigureAwait(false);
                return;
            }

            var handler = scope.ServiceProvider.GetRequiredService<IOrderEventHandler>();
            await handler.HandleAsync(notification, stoppingToken).ConfigureAwait(false);

            dbContext.ProcessedMessages.Add(new ProcessedMessage
            {
                MessageId = notification.EventId,
                ProcessedAt = timeProvider.GetUtcNow()
            });

            await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);

            OrderTrackingDiagnostics.MessageHandled("handled");

            // Acknowledged only now. Anything that went wrong above leaves the message
            // unacknowledged, and the broker redelivers it.
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. Leave the message unacknowledged so it is redelivered.
        }
#pragma warning disable CA1031 // An unhandled exception here would tear down the dispatcher.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogHandlingFailed(logger, delivery.RoutingKey, exception);
            OrderTrackingDiagnostics.MessageHandled("failed");
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);

            // requeue: false sends it to the dead-letter exchange. Requeueing a message that
            // fails deterministically is an infinite loop that saturates the consumer and
            // hides every message behind it.
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static ActivityContext ParentOf(BasicDeliverEventArgs delivery)
    {
        if (delivery.BasicProperties.Headers?.TryGetValue("traceparent", out var raw) != true
            || raw is not byte[] utf8)
        {
            return default;
        }

        return ActivityContext.TryParse(Encoding.UTF8.GetString(utf8), traceState: null, out var context)
            ? context
            : default;
    }

    private static async Task<bool> AlreadyHandledAsync(
        OrderTrackingDbContext dbContext, Guid eventId, CancellationToken cancellationToken) =>
        await dbContext.ProcessedMessages
            .AsNoTracking()
            .AnyAsync(message => message.MessageId == eventId, cancellationToken)
            .ConfigureAwait(false);

    private static OrderEventNotification? Parse(BasicDeliverEventArgs delivery)
    {
        var json = Encoding.UTF8.GetString(delivery.Body.Span);

        try
        {
            // Switching on the type name carried alongside the payload, rather than on
            // polymorphic JSON. The alternative bakes .NET type names into the wire format
            // and lets a message name any type it likes for the deserializer to construct.
            return delivery.BasicProperties.Type switch
            {
                nameof(OrderCreatedEvent) =>
                    ToNotification(JsonSerializer.Deserialize<OrderCreatedEvent>(
                        json, OutboxSerialization.Options)),
                nameof(OrderStatusChangedEvent) =>
                    ToNotification(JsonSerializer.Deserialize<OrderStatusChangedEvent>(
                        json, OutboxSerialization.Options)),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static OrderEventNotification? ToNotification(OrderCreatedEvent? domainEvent) =>
        domainEvent is null
            ? null
            : new OrderEventNotification(
                domainEvent.EventId,
                domainEvent.OrderNumber,
                domainEvent.Status,
                PreviousStatus: null,
                domainEvent.OccurredAt);

    private static OrderEventNotification? ToNotification(OrderStatusChangedEvent? domainEvent) =>
        domainEvent is null
            ? null
            : new OrderEventNotification(
                domainEvent.EventId,
                domainEvent.OrderNumber,
                domainEvent.NewStatus,
                domainEvent.OldStatus,
                domainEvent.OccurredAt);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Consuming {Queue} with a prefetch of {PrefetchCount}.")]
    private static partial void LogConsuming(ILogger logger, string queue, ushort prefetchCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Order event consumer stopping.")]
    private static partial void LogStopping(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Event {EventId} was already handled; acknowledging the duplicate.")]
    private static partial void LogDuplicate(ILogger logger, Guid eventId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Message with routing key {RoutingKey} could not be read; dead-lettering it.")]
    private static partial void LogUnreadable(ILogger logger, string routingKey);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Handling the message with routing key {RoutingKey} failed; dead-lettering it.")]
    private static partial void LogHandlingFailed(ILogger logger, string routingKey, Exception exception);
}

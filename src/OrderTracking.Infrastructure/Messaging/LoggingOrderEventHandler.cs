using Microsoft.Extensions.Logging;
using OrderTracking.Domain.Orders;

namespace OrderTracking.Infrastructure.Messaging;

/// <summary>
/// Writes each order event to the log.
/// </summary>
/// <param name="logger">Receives the events.</param>
/// <remarks>
/// The fallback registered by the messaging layer. A host with somewhere better to send
/// events — the API registers its WebSocket broadcaster — replaces it; any host that uses
/// this layer without one still ends the pipeline somewhere observable rather than nowhere.
/// </remarks>
public sealed partial class LoggingOrderEventHandler(ILogger<LoggingOrderEventHandler> logger)
    : IOrderEventHandler
{
    /// <inheritdoc />
    public Task HandleAsync(OrderEventNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        LogNotification(
            logger,
            notification.OrderNumber,
            notification.PreviousStatus,
            notification.Status,
            notification.EventId);

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Order {OrderNumber} moved from {PreviousStatus} to {Status} (event {EventId}).")]
    private static partial void LogNotification(
        ILogger logger, string orderNumber, OrderStatus? previousStatus, OrderStatus status, Guid eventId);
}

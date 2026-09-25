using Microsoft.Extensions.Logging;
using OrderTracking.Domain.Orders;

namespace OrderTracking.Infrastructure.Messaging;

/// <summary>
/// Writes each order event to the log.
/// </summary>
/// <param name="logger">Receives the events.</param>
/// <remarks>
/// The default handler until the WebSocket broadcaster replaces it. Keeping a real
/// implementation registered from the start means the consumer end of the pipeline is
/// exercised and observable now, instead of being first tried when there is also new
/// socket code to blame.
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

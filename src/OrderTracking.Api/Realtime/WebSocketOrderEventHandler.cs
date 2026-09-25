using Microsoft.EntityFrameworkCore;
using OrderTracking.Api.Contracts.Orders;
using OrderTracking.Infrastructure.Diagnostics;
using OrderTracking.Infrastructure.Messaging;
using OrderTracking.Infrastructure.Persistence;

namespace OrderTracking.Api.Realtime;

/// <summary>
/// Turns an order event taken off the queue into a frame on every open socket.
/// </summary>
/// <param name="dbContext">Reads the order the event refers to.</param>
/// <param name="connections">The clients to notify.</param>
/// <param name="logger">Receives events that refer to orders which no longer exist.</param>
/// <remarks>
/// <para>
/// This is the implementation the layering was arranged for: declared as
/// <see cref="IOrderEventHandler"/> in Infrastructure, implemented here in the API, because
/// the consumer cannot reference this project but has to end up calling it.
/// </para>
/// <para>
/// The current row is read rather than the event being translated field by field. The event
/// says what happened; the client needs what is true now, including the description, which
/// the notification does not carry. Reading also means a burst of events converges on the
/// latest state instead of replaying a sequence the user does not care about.
/// </para>
/// <para>
/// The per-status totals are read in the same scope, so they describe the same state the
/// frame does.
/// </para>
/// </remarks>
public sealed partial class WebSocketOrderEventHandler(
    OrderTrackingDbContext dbContext,
    OrderSocketConnectionManager connections,
    ILogger<WebSocketOrderEventHandler> logger) : IOrderEventHandler
{
    /// <inheritdoc />
    public async Task HandleAsync(OrderEventNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // The last span of the chain. Its parent is the consumer's, whose parent is the
        // publisher's, whose parent is the HTTP request that changed the status — so one
        // trace covers the whole distance from the user's click to the frame on every screen.
        using var activity = OrderTrackingDiagnostics.StartActivity("socket broadcast");

        activity?.SetTag("ordertracking.order_number", notification.OrderNumber);
        activity?.SetTag("ordertracking.socket.clients", connections.Count);

        var order = await dbContext.Orders
            .AsNoTracking()
            .Where(candidate => candidate.OrderNumber == notification.OrderNumber)
            .Select(candidate => new OrderSummaryResponse(
                candidate.OrderNumber,
                candidate.Description,
                candidate.Status,
                candidate.CreatedAt,
                candidate.UpdatedAt))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (order is null)
        {
            // Possible in principle — an event outlives the row it describes
            LogMissingOrder(logger, notification.OrderNumber, notification.EventId);
            return;
        }

        // Read after the order and in the same scope, so the totals describe the same state
        // the frame does. A second query per event is the price of the client never having
        // to work the numbers out from a page it only partly holds.
        var counts = await OrderStatusCountReader.ReadAsync(dbContext, cancellationToken)
            .ConfigureAwait(false);

        connections.Broadcast(order, counts);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Order {OrderNumber} from event {EventId} no longer exists; nothing was broadcast.")]
    private static partial void LogMissingOrder(ILogger logger, string orderNumber, Guid eventId);
}

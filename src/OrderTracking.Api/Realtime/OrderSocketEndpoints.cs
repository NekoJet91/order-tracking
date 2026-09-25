using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderTracking.Api.Contracts.Orders;
using OrderTracking.Infrastructure.Diagnostics;
using OrderTracking.Infrastructure.Persistence;

namespace OrderTracking.Api.Realtime;

/// <summary>
/// The notification socket clients subscribe to.
/// </summary>
public static class OrderSocketEndpoints
{
    /// <summary>Maps <c>/ws/orders</c>.</summary>
    /// <param name="endpoints">The route builder to add to.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapOrderSocketEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.Map("/ws/orders", HandleAsync)
            .WithTags("Orders")
            .ExcludeFromDescription();

        return endpoints;
    }

    /// <remarks>
    /// Excluded from the OpenAPI document deliberately: the handshake is an HTTP GET that
    /// never returns a body, and describing it as one would tell a generated client
    /// something untrue. The frame shapes are documented on the message records instead.
    /// </remarks>
    private static async Task HandleAsync(
        HttpContext context,
        OrderSocketConnectionManager connections,
        IServiceScopeFactory scopeFactory,
        IOptions<OrderSocketOptions> socketOptions,
        IHostApplicationLifetime lifetime)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "Expected a WebSocket upgrade request." });
            return;
        }

        var options = socketOptions.Value;

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await using var connection = new OrderSocketConnection(socket, options.SendQueueCapacity);

        // Registered before the snapshot is read, not after. The other order leaves a gap in
        // which a status change is missed by both: too late for the snapshot, too early for
        // the subscription. Registering first can only produce a duplicate, and the client
        // discards those by comparing updatedAt.
        connections.Add(connection);

        try
        {
            var (orders, counts) =
                await ReadSnapshotAsync(scopeFactory, options.SnapshotSize, context.RequestAborted);

            // Written straight to the socket rather than queued, so it is the first frame
            // even though deltas may already be sitting in the queue behind it.
            await connection.SendImmediateAsync(
                connections.Serialize(new OrderSnapshotMessage(orders, counts)),
                context.RequestAborted);

            OrderTrackingDiagnostics.FramesSent("snapshot", 1);

            // Linked to application shutdown as well as the request, so a stopping host
            // closes sockets instead of waiting on clients that have no reason to leave.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, lifetime.ApplicationStopping);

            await connection.RunAsync(linked.Token);
        }
        finally
        {
            connections.Remove(connection);
        }
    }

    /// <remarks>
    /// Read in its own scope, which is then disposed, rather than through a context injected
    /// for the whole request. A socket request's scope lives as long as the connection —
    /// potentially hours — and there is no reason for a change tracker to live that long.
    /// The totals share that scope: two reads, one connection, one lifetime.
    /// </remarks>
    private static async Task<(IReadOnlyList<OrderSummaryResponse> Orders, IReadOnlyDictionary<string, int> Counts)>
        ReadSnapshotAsync(IServiceScopeFactory scopeFactory, int size, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();

        var orders = await dbContext.Orders
            .AsNoTracking()
            .OrderByDescending(order => order.CreatedAt)
            .ThenByDescending(order => order.Id)
            .Take(size)
            .Select(order => new OrderSummaryResponse(
                order.OrderNumber,
                order.Description,
                order.Status,
                order.CreatedAt,
                order.UpdatedAt))
            .ToListAsync(cancellationToken);

        var counts = await OrderStatusCountReader.ReadAsync(dbContext, cancellationToken);

        return (orders, counts);
    }
}

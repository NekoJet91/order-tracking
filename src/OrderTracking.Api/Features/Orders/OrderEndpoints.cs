using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using OrderTracking.Api.Contracts.Orders;
using OrderTracking.Api.Http;
using OrderTracking.Domain.Orders;
using OrderTracking.Infrastructure;
using OrderTracking.Infrastructure.Persistence;

namespace OrderTracking.Api.Features.Orders;

/// <summary>
/// The order resource: creation, listing, retrieval and status changes.
/// </summary>
public static class OrderEndpoints
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    /// <summary>
    /// Maps every endpoint under <c>/api/orders</c>.
    /// </summary>
    /// <param name="endpoints">The route builder to add to.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/orders").WithTags("Orders");

        group.MapPost("/", CreateOrderAsync)
            .AddEndpointFilter<ValidationFilter<CreateOrderRequest>>()
            .WithName("CreateOrder")
            .WithSummary("Places a new order.")
            .WithDescription(
                "The order number is allocated by the server and returned in the Location header. "
                + "New orders always start in the Created status.")
            .ProducesValidationProblem();

        group.MapGet("/", ListOrdersAsync)
            .WithName("ListOrders")
            .WithSummary("Lists orders, newest first.")
            .WithDescription(
                "Keyset paginated. Pass the nextCursor from the previous response to walk "
                + "forward; a null nextCursor means there are no more rows.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/{orderNumber}", GetOrderAsync)
            .WithName("GetOrder")
            .WithSummary("Fetches a single order by its order number.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPatch("/{orderNumber}/status", ChangeOrderStatusAsync)
            .AddEndpointFilter<ValidationFilter<ChangeOrderStatusRequest>>()
            .WithName("ChangeOrderStatus")
            .WithSummary("Moves an order to a different status.")
            .WithDescription(
                "Requesting the status the order is already in succeeds and changes nothing, "
                + "so a client that retries after a timeout is not punished for it.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<Created<OrderDetailsResponse>> CreateOrderAsync(
        CreateOrderRequest request,
        OrderTrackingDbContext dbContext,
        IOrderNumberGenerator orderNumbers,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var orderNumber = await orderNumbers.NextAsync(cancellationToken);

        // Trimming is normalization of untrusted input and belongs at the boundary.
        var order = Order.Create(orderNumber, request.Description.Trim(), timeProvider.GetStorableUtcNow());

        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/orders/{order.OrderNumber}", ToDetails(order));
    }

    private static async Task<Results<Ok<OrderPageResponse>, ProblemHttpResult>> ListOrdersAsync(
        OrderTrackingDbContext dbContext,
        CancellationToken cancellationToken,
        OrderStatus? status = null,
        string? cursor = null,
        int limit = DefaultPageSize)
    {
        if (limit is < 1 or > MaxPageSize)
        {
            return TypedResults.Problem(
                title: "Invalid page size",
                detail: $"limit must be between 1 and {MaxPageSize}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (status is not null && !Enum.IsDefined(status.Value))
        {
            // An undefined enum prints as its number, which is exactly what the caller sent.
            return TypedResults.Problem(
                title: "Unknown status",
                detail: $"'{status}' is not a known order status.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var query = dbContext.Orders.AsQueryable();

        if (status is not null)
        {
            query = query.Where(order => order.Status == status.Value);
        }

        if (cursor is not null)
        {
            if (!OrderCursor.TryDecode(cursor, out var after))
            {
                return TypedResults.Problem(
                    title: "Invalid cursor",
                    detail: "The cursor is not a token this API issued. Start from the first page.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // The row-value form (created_at, id) < (@createdAt, @id) is what PostgreSQL
            // handles best, but EF cannot emit tuple comparison, so it is spelled out. The
            // shape still matches ix_orders_created_at_id_desc.
            query = query.Where(order =>
                order.CreatedAt < after.CreatedAt
                || (order.CreatedAt == after.CreatedAt && order.Id < after.Id));
        }

        // Projected into OrderRow rather than through SelectSummary, because the next cursor
        // needs the Id and the summary does not carry it.
        var rows = await query
            .OrderByDescending(order => order.CreatedAt)
            .ThenByDescending(order => order.Id)
            .Select(order => new OrderRow(
                order.Id,
                order.OrderNumber,
                order.Description,
                order.Status,
                order.CreatedAt,
                order.UpdatedAt))
            // One extra row is the cheap way to know next page exists, no counting the table
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = rows
            .Select(row => new OrderSummaryResponse(
                row.OrderNumber, row.Description, row.Status, row.CreatedAt, row.UpdatedAt))
            .ToArray();

        var nextCursor = hasMore
            ? new OrderCursor(rows[^1].CreatedAt, rows[^1].Id).Encode()
            : null;

        return TypedResults.Ok(new OrderPageResponse(items, nextCursor));
    }

    private static async Task<Results<Ok<OrderDetailsResponse>, ProblemHttpResult>> GetOrderAsync(
        string orderNumber,
        OrderTrackingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var order = await dbContext.Orders
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.OrderNumber == orderNumber, cancellationToken);

        return order is null
            ? OrderNotFound(orderNumber)
            : TypedResults.Ok(ToDetails(order));
    }

    private static async Task<Results<Ok<OrderDetailsResponse>, ProblemHttpResult>> ChangeOrderStatusAsync(
        string orderNumber,
        ChangeOrderStatusRequest request,
        OrderTrackingDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var order = await dbContext.Orders
            .SingleOrDefaultAsync(candidate => candidate.OrderNumber == orderNumber, cancellationToken);

        if (order is null)
        {
            return OrderNotFound(orderNumber);
        }

        try
        {
            order.ChangeStatus(request.Status, timeProvider.GetStorableUtcNow());
        }
        catch (InvalidOrderStatusTransitionException exception)
        {
            return TypedResults.Problem(
                title: "Status change not permitted",
                detail: exception.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["orderNumber"] = orderNumber,
                    ["currentStatus"] = exception.From.ToString(),
                    ["requestedStatus"] = exception.To.ToString(),
                    ["allowedNextStatuses"] = AllowedNextNames(exception.From)
                });
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Not retried on the server. A retry would re-read the order and re-apply the
            // transition, which may have become illegal — or worse, still legal but no
            // longer what the user was looking at when they clicked.
            return TypedResults.Problem(
                title: "Order was modified concurrently",
                detail: $"Order '{orderNumber}' changed while this request was in flight. "
                        + "Re-read it and retry if the change still applies.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["orderNumber"] = orderNumber });
        }

        return TypedResults.Ok(ToDetails(order));
    }

    private static ProblemHttpResult OrderNotFound(string orderNumber) =>
        TypedResults.Problem(
            title: "Order not found",
            detail: $"No order with number '{orderNumber}' exists.",
            statusCode: StatusCodes.Status404NotFound,
            extensions: new Dictionary<string, object?> { ["orderNumber"] = orderNumber });

    private static OrderDetailsResponse ToDetails(Order order) =>
        new(order.OrderNumber,
            order.Description,
            order.Status,
            AllowedNext(order.Status),
            order.CreatedAt,
            order.UpdatedAt);

    /// <summary>The permitted next statuses in a stable order, so responses are comparable.</summary>
    private static OrderStatus[] AllowedNext(OrderStatus from) =>
        [.. OrderStatusTransitions.AllowedFrom(from).OrderBy(status => status)];

    /// <summary>
    /// The same list as names. Problem-details extensions are an untyped bag, so the values
    /// are spelled out here rather than left to whichever serializer options write the body.
    /// </summary>
    private static string[] AllowedNextNames(OrderStatus from) =>
        [.. AllowedNext(from).Select(status => status.ToString())];

    /// <summary>
    /// Flat projection of the columns the list endpoint reads. Carries <c>Id</c>, which the
    /// API never returns, only so the next cursor can be built from the last row.
    /// </summary>
    private sealed record OrderRow(
        long Id,
        string OrderNumber,
        string Description,
        OrderStatus Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}

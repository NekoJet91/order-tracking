using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderTracking.Api.Contracts.Orders;
using OrderTracking.Domain.Orders;
using OrderTracking.Infrastructure.Outbox;
using OrderTracking.Infrastructure.Persistence;

namespace OrderTracking.IntegrationTests.Api;

/// <summary>
/// Drives the HTTP surface end to end, from JSON in to rows in PostgreSQL and back.
/// </summary>
/// <remarks>
/// The fixture empties the database once per class, not once per test, so these tests
/// assert on the orders they created and on invariants that hold whatever else is in the
/// table. That is deliberate: a test that only passes when the table is otherwise empty
/// stops being true the moment the suite grows.
/// </remarks>
public sealed class OrderEndpointsTests(OrderTrackingApiFactory factory)
    : IClassFixture<OrderTrackingApiFactory>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private HttpClient Client => factory.Client;

    [Fact]
    public async Task Creating_an_order_returns_201_with_a_location_header()
    {
        var response = await PostOrderAsync("Кресло офисное, чёрное");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await ReadAsync<OrderDetailsResponse>(response);

        Assert.Matches(@"^ORD-\d{8}$", created.OrderNumber);
        Assert.Equal(OrderStatus.Created, created.Status);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);
        Assert.Equal(
            [OrderStatus.Shipped, OrderStatus.Cancelled],
            created.AllowedNextStatuses.Order());
        Assert.Equal($"/api/orders/{created.OrderNumber}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Created_order_can_be_fetched_at_its_location()
    {
        var created = await CreateOrderAsync("Монитор 27\", 4K");

        var fetched = await Client.GetFromJsonAsync<OrderDetailsResponse>(
            $"/api/orders/{created.OrderNumber}", OrderTrackingApiFactory.Json, Cancellation);

        AssertSame(created, fetched);
    }

    [Fact]
    public async Task Unknown_order_number_returns_404_as_problem_details()
    {
        var response = await Client.GetAsync("/api/orders/ORD-99999999", Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await ReadAsync<ProblemDetails>(response);

        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
        Assert.Equal("ORD-99999999", Extension(problem, "orderNumber"));
        Assert.False(string.IsNullOrWhiteSpace(Extension(problem, "traceId")));
    }

    [Fact]
    public async Task Blank_description_is_rejected_before_anything_is_written()
    {
        var response = await PostOrderAsync("   ");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadAsync<HttpValidationProblemDetails>(response);

        Assert.Contains(nameof(CreateOrderRequest.Description), problem.Errors.Keys);
    }

    [Fact]
    public async Task Description_over_the_limit_is_rejected()
    {
        var response = await PostOrderAsync(new string('a', Order.DescriptionMaxLength + 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Legal_status_change_is_applied_and_moves_updated_at()
    {
        var created = await CreateOrderAsync("Клавиатура механическая");

        var updated = await ChangeStatusAsync(created.OrderNumber, OrderStatus.Shipped);

        Assert.Equal(OrderStatus.Shipped, updated.Status);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt > created.UpdatedAt);
        Assert.Equal([OrderStatus.Delivered, OrderStatus.Cancelled], updated.AllowedNextStatuses.Order());
    }

    [Fact]
    public async Task Repeating_the_current_status_succeeds_and_changes_nothing()
    {
        var created = await CreateOrderAsync("Мышь беспроводная");

        var repeated = await ChangeStatusAsync(created.OrderNumber, OrderStatus.Created);

        AssertSame(created, repeated);
    }

    [Fact]
    public async Task Illegal_status_change_returns_409_naming_the_legal_ones()
    {
        var created = await CreateOrderAsync("Док-станция USB-C");

        var response = await PatchStatusAsync(created.OrderNumber, OrderStatus.Delivered);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await ReadAsync<ProblemDetails>(response);

        Assert.Equal(nameof(OrderStatus.Created), Extension(problem, "currentStatus"));
        Assert.Equal(nameof(OrderStatus.Delivered), Extension(problem, "requestedStatus"));

        var allowed = problem.Extensions["allowedNextStatuses"] as JsonElement?;
        Assert.Equal(
            [nameof(OrderStatus.Shipped), nameof(OrderStatus.Cancelled)],
            allowed!.Value.EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task Status_change_on_an_unknown_order_returns_404()
    {
        var response = await PatchStatusAsync("ORD-99999999", OrderStatus.Shipped);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_status_name_is_rejected()
    {
        var response = await Client.PatchAsJsonAsync(
            "/api/orders/ORD-00000001/status", new { status = "Teleported" }, Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(2)]  // a value that is defined, but still not part of the string contract
    [InlineData(7)]  // and one that is not defined at all
    public async Task Numeric_status_is_rejected_whatever_the_number(int status)
    {
        var response = await Client.PatchAsJsonAsync(
            "/api/orders/ORD-00000001/status", new { status }, Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_returns_newest_first_and_pages_through_the_cursor()
    {
        var mine = new List<string>();
        for (var i = 1; i <= 5; i++)
        {
            mine.Add((await CreateOrderAsync($"Позиция для проверки страниц №{i}")).OrderNumber);
        }

        var seen = new List<OrderSummaryResponse>();
        string? cursor = null;

        // Bounded so a broken cursor fails the test instead of hanging the suite.
        for (var page = 0; page < 20; page++)
        {
            var url = cursor is null
                ? "/api/orders?limit=2"
                : $"/api/orders?limit=2&cursor={Uri.EscapeDataString(cursor)}";

            var body = await Client.GetFromJsonAsync<OrderPageResponse>(
                url, OrderTrackingApiFactory.Json, Cancellation);

            Assert.NotNull(body);
            Assert.True(body.Items.Count <= 2);

            seen.AddRange(body.Items);
            cursor = body.NextCursor;

            if (cursor is null)
            {
                break;
            }
        }

        Assert.Null(cursor);

        // No row was skipped or handed out twice while walking the pages.
        Assert.Distinct(seen.Select(item => item.OrderNumber));
        Assert.All(mine, orderNumber => Assert.Contains(seen, item => item.OrderNumber == orderNumber));

        // The global ordering invariant holds across page boundaries, which is the whole
        // point of keyset pagination.
        var timestamps = seen.Select(item => item.CreatedAt).ToArray();
        Assert.Equal(timestamps.OrderDescending(), timestamps);

        // And within the orders this test created, newest really is first.
        var minePositions = mine
            .Select(number => seen.FindIndex(item => item.OrderNumber == number))
            .ToArray();

        Assert.Equal(minePositions.OrderDescending(), minePositions);
    }

    [Fact]
    public async Task List_can_be_filtered_by_status()
    {
        var shipped = await CreateOrderAsync("Заказ, который отправим");
        var untouched = await CreateOrderAsync("Заказ, который оставим как есть");

        await ChangeStatusAsync(shipped.OrderNumber, OrderStatus.Shipped);

        var body = await Client.GetFromJsonAsync<OrderPageResponse>(
            "/api/orders?status=Shipped&limit=100", OrderTrackingApiFactory.Json, Cancellation);

        Assert.NotNull(body);
        Assert.All(body.Items, item => Assert.Equal(OrderStatus.Shipped, item.Status));
        Assert.Contains(body.Items, item => item.OrderNumber == shipped.OrderNumber);
        Assert.DoesNotContain(body.Items, item => item.OrderNumber == untouched.OrderNumber);
    }

    [Fact]
    public async Task Creating_an_order_writes_a_pending_outbox_row()
    {
        var created = await CreateOrderAsync("Заказ, порождающий событие");

        var message = await SingleOutboxMessageAsync(created.OrderNumber);

        Assert.Equal("OrderCreatedEvent", message.Type);
        Assert.Null(message.ProcessedAt);
        Assert.Equal(0, message.AttemptCount);

        var payload = Payload(message);

        Assert.Equal(created.OrderNumber, payload.GetProperty("orderNumber").GetString());
        Assert.Equal("Created", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Changing_status_writes_an_outbox_row_carrying_both_statuses()
    {
        var created = await CreateOrderAsync("Заказ, меняющий статус");
        await ChangeStatusAsync(created.OrderNumber, OrderStatus.Shipped);

        var messages = await OutboxMessagesAsync(created.OrderNumber);

        Assert.Equal(["OrderCreatedEvent", "OrderStatusChangedEvent"], messages.Select(m => m.Type));

        var change = Payload(messages[1]);

        Assert.Equal("Created", change.GetProperty("oldStatus").GetString());
        Assert.Equal("Shipped", change.GetProperty("newStatus").GetString());
    }

    [Fact]
    public async Task Rejected_status_change_writes_no_outbox_row()
    {
        var created = await CreateOrderAsync("Заказ с недопустимым переходом");

        var response = await PatchStatusAsync(created.OrderNumber, OrderStatus.Delivered);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // The point of writing the event in the same transaction: a change that did not
        // happen cannot leave an event behind claiming it did.
        var messages = await OutboxMessagesAsync(created.OrderNumber);
        Assert.Equal(["OrderCreatedEvent"], messages.Select(m => m.Type));
    }

    [Theory]
    [InlineData("/api/orders?limit=0")]
    [InlineData("/api/orders?limit=101")]
    [InlineData("/api/orders?cursor=not-a-cursor")]
    [InlineData("/api/orders?status=Teleported")]
    [InlineData("/api/orders?status=7")]
    public async Task Malformed_query_parameters_return_400(string url)
    {
        var response = await Client.GetAsync(url, Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    //Post, Patch => for tests where we assert on the response itself (status, headers, problem body)
    //CreateOrder, ChangeStatus => tests where it's expected to be successful, and we are looking at the specifics

    private Task<HttpResponseMessage> PostOrderAsync(string description) =>
        Client.PostAsJsonAsync(
            "/api/orders", new CreateOrderRequest(description), OrderTrackingApiFactory.Json, Cancellation);

    private Task<HttpResponseMessage> PatchStatusAsync(string orderNumber, OrderStatus status) =>
        Client.PatchAsJsonAsync(
            $"/api/orders/{orderNumber}/status",
            new ChangeOrderStatusRequest(status),
            OrderTrackingApiFactory.Json,
            Cancellation);

    private async Task<OrderDetailsResponse> CreateOrderAsync(string description)
    {
        var response = await PostOrderAsync(description);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<OrderDetailsResponse>(response);
    }

    private async Task<OrderDetailsResponse> ChangeStatusAsync(string orderNumber, OrderStatus status)
    {
        var response = await PatchStatusAsync(orderNumber, status);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<OrderDetailsResponse>(response);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(
            OrderTrackingApiFactory.Json, Cancellation);

        Assert.NotNull(value);
        return value;
    }

    /// <summary>
    /// Outbox rows mentioning one order, oldest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The outbox is deliberately generic — a type name and a JSON body, with no column that
    /// knows what an order number is — so the filter reaches into the payload. Filtering in
    /// the database keeps the rows written by every other test out of the way.
    /// </para>
    /// <para>
    /// Raw SQL rather than LINQ: <c>payload</c> is <c>jsonb</c>, and EF would translate
    /// <c>string.Contains</c> to <c>LIKE</c>, which PostgreSQL does not define for that type.
    /// The <c>-&gt;&gt;</c> operator extracts the field as text and matches it exactly, which
    /// is also the payoff for having chosen jsonb over a plain text column.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<OutboxMessage>> OutboxMessagesAsync(string orderNumber)
    {
        await using var scope = factory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();

        return await dbContext.OutboxMessages
            .FromSql($"""
                SELECT   *
                FROM     outbox_messages
                WHERE    payload ->> 'orderNumber' = {orderNumber}
                ORDER BY id
                """)
            .AsNoTracking()
            .ToListAsync(Cancellation);
    }

    private async Task<OutboxMessage> SingleOutboxMessageAsync(string orderNumber) =>
        Assert.Single(await OutboxMessagesAsync(orderNumber));

    /// <summary>
    /// Parses a stored payload.
    /// </summary>
    /// <remarks>
    /// Parsed rather than matched as a string, because <c>jsonb</c> is not a text column.
    /// PostgreSQL reparses the document on the way in and stores a binary form, so what
    /// comes back is its canonical rendering — keys reordered by length, a space after every
    /// colon — and not the text that was written. A substring assertion here would be
    /// testing PostgreSQL's formatter rather than our serializer.
    /// </remarks>
    private static JsonElement Payload(OutboxMessage message) =>
        JsonDocument.Parse(message.Payload).RootElement;

    private static string? Extension(ProblemDetails problem, string name) =>
        problem.Extensions[name] is JsonElement element ? element.GetString() : null;

    /// <summary>
    /// Compares two responses member by member.
    /// </summary>
    /// <remarks>
    /// Not <c>Assert.Equal</c> on the records themselves, that is reference equality
    /// </remarks>
    private static void AssertSame(OrderDetailsResponse expected, OrderDetailsResponse? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.OrderNumber, actual.OrderNumber);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.AllowedNextStatuses, actual.AllowedNextStatuses);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
    }
}

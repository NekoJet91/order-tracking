using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrderTracking.Api.Contracts.Orders;
using OrderTracking.Api.Realtime;
using OrderTracking.Domain.Orders;
using OrderTracking.Infrastructure.Messaging;

namespace OrderTracking.IntegrationTests.Api;

/// <summary>
/// Exercises <c>/ws/orders</c> over a real WebSocket against the in-memory host.
/// </summary>
/// <remarks>
/// The broker is not involved. These tests are about what reaches a browser once an event
/// has been handed to the handler; the path from RabbitMQ to that handler has its own
/// fixture. Invoking the handler directly is what keeps the two concerns separately
/// diagnosable — a failure here is socket code, not delivery.
/// </remarks>
public sealed class OrderSocketTests(OrderTrackingApiFactory factory) : IClassFixture<OrderTrackingApiFactory>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Connecting_receives_a_snapshot_containing_existing_orders()
    {
        var first = await CreateOrderAsync("Щит распределительный IP54");
        var second = await CreateOrderAsync("Гофротруба ПВХ 25 мм");

        using var socket = await ConnectAsync();
        var snapshot = await ReceiveAsync(socket, "snapshot");

        var numbers = snapshot.GetProperty("orders")
            .EnumerateArray()
            .Select(order => order.GetProperty("orderNumber").GetString())
            .ToArray();

        Assert.Contains(first, numbers);
        Assert.Contains(second, numbers);

        // Newest first, the same order the list endpoint uses, so a client can render the
        // snapshot without sorting it again.
        Assert.Equal(second, numbers[0]);
    }

    [Fact]
    public async Task The_snapshot_spells_a_status_the_way_the_rest_api_does()
    {
        await CreateOrderAsync("Лоток кабельный оцинкованный");

        using var socket = await ConnectAsync();
        var snapshot = await ReceiveAsync(socket, "snapshot");

        var status = snapshot.GetProperty("orders")[0].GetProperty("status");

        // A number here would mean the socket and the REST API had been configured
        // separately, which is how a client ends up parsing one shape in two ways.
        Assert.Equal(JsonValueKind.String, status.ValueKind);
        Assert.Equal(nameof(OrderStatus.Created), status.GetString());
    }

    [Fact]
    public async Task A_status_change_is_pushed_to_a_connected_client()
    {
        var orderNumber = await CreateOrderAsync("Автоматический выключатель C16");

        using var socket = await ConnectAsync();
        await ReceiveAsync(socket, "snapshot");

        await ChangeStatusAsync(orderNumber, OrderStatus.Shipped);
        await PublishEventAsync(orderNumber, OrderStatus.Shipped, OrderStatus.Created);

        var frame = await ReceiveAsync(socket, "order-changed");
        var order = frame.GetProperty("order");

        Assert.Equal(orderNumber, order.GetProperty("orderNumber").GetString());
        Assert.Equal(nameof(OrderStatus.Shipped), order.GetProperty("status").GetString());

        // The whole order travels, not just the status, so a client seeing this order for
        // the first time can render it without a follow-up request.
        Assert.Equal("Автоматический выключатель C16", order.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Every_connected_client_receives_the_change()
    {
        var orderNumber = await CreateOrderAsync("Светильник светодиодный 36 Вт");

        using var first = await ConnectAsync();
        using var second = await ConnectAsync();

        await ReceiveAsync(first, "snapshot");
        await ReceiveAsync(second, "snapshot");

        await ChangeStatusAsync(orderNumber, OrderStatus.Shipped);
        await PublishEventAsync(orderNumber, OrderStatus.Shipped, OrderStatus.Created);

        foreach (var socket in new[] { first, second })
        {
            var order = (await ReceiveAsync(socket, "order-changed")).GetProperty("order");
            Assert.Equal(orderNumber, order.GetProperty("orderNumber").GetString());
        }
    }

    [Fact]
    public async Task An_event_for_a_deleted_order_broadcasts_nothing()
    {
        var present = await CreateOrderAsync("Стяжка кабельная 300 мм");

        using var socket = await ConnectAsync();
        await ReceiveAsync(socket, "snapshot");

        // An order number that was never issued stands in for a row that has gone away.
        await PublishEventAsync("ORD-99999999", OrderStatus.Delivered, OrderStatus.Shipped);

        // Proving a negative directly is not possible, so a real event is sent afterwards
        // and the first frame to arrive is required to be that one.
        await ChangeStatusAsync(present, OrderStatus.Shipped);
        await PublishEventAsync(present, OrderStatus.Shipped, OrderStatus.Created);

        var order = (await ReceiveAsync(socket, "order-changed")).GetProperty("order");
        Assert.Equal(present, order.GetProperty("orderNumber").GetString());
    }

    /// <remarks>
    /// Measured as a difference rather than against a literal. The database is truncated once
    /// per class, not per test, so every absolute count here carries whatever earlier tests
    /// left behind — and a test that depends on its neighbours is a test that fails when one
    /// is added.
    /// </remarks>
    [Fact]
    public async Task The_snapshot_counts_orders_beyond_the_ones_it_sends()
    {
        var size = factory.Services.GetRequiredService<IOptions<OrderSocketOptions>>().Value.SnapshotSize;

        int Created(JsonElement frame) =>
            frame.GetProperty("counts").GetProperty(nameof(OrderStatus.Created)).GetInt32();

        int before;

        using (var baseline = await ConnectAsync())
        {
            before = Created(await ReceiveAsync(baseline, "snapshot"));
        }

        // More new orders than one snapshot can carry, so counting the frame's own array
        // would give a different — and wrong — answer from counting the table.
        for (var i = 0; i < size + 3; i++)
        {
            await CreateOrderAsync($"Клемма WAGO 221, партия {i}");
        }

        using var socket = await ConnectAsync();
        var snapshot = await ReceiveAsync(socket, "snapshot");

        Assert.Equal(size, snapshot.GetProperty("orders").GetArrayLength());
        Assert.Equal(before + size + 3, Created(snapshot));
    }

    [Fact]
    public async Task The_counts_include_every_status()
    {
        await CreateOrderAsync("Розетка силовая 32 А");

        using var socket = await ConnectAsync();
        var counts = (await ReceiveAsync(socket, "snapshot")).GetProperty("counts");

        // Built from the enum rather than from the rows that happen to exist, so a status
        // nothing is in appears as a zero instead of being absent. That is what lets the
        // client index any status without first checking the key is there.
        foreach (var status in Enum.GetValues<OrderStatus>())
        {
            Assert.True(counts.TryGetProperty(status.ToString(), out _), $"{status} missing");
        }
    }

    [Fact]
    public async Task A_change_frame_carries_the_totals_after_the_change()
    {
        var orderNumber = await CreateOrderAsync("Кабель-канал 40×25");

        using var socket = await ConnectAsync();
        var before = (await ReceiveAsync(socket, "snapshot")).GetProperty("counts");

        var createdBefore = before.GetProperty(nameof(OrderStatus.Created)).GetInt32();
        var shippedBefore = before.GetProperty(nameof(OrderStatus.Shipped)).GetInt32();

        await ChangeStatusAsync(orderNumber, OrderStatus.Shipped);
        await PublishEventAsync(orderNumber, OrderStatus.Shipped, OrderStatus.Created);

        var after = (await ReceiveAsync(socket, "order-changed")).GetProperty("counts");

        // The totals move with the order, so a client never has to work them out from the
        // page it holds — which would be a page, not a total.
        Assert.Equal(createdBefore - 1, after.GetProperty(nameof(OrderStatus.Created)).GetInt32());
        Assert.Equal(shippedBefore + 1, after.GetProperty(nameof(OrderStatus.Shipped)).GetInt32());
    }

    [Fact]
    public async Task A_plain_GET_is_rejected()
    {
        var response = await factory.Client.GetAsync(new Uri("/ws/orders", UriKind.Relative), Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<WebSocket> ConnectAsync()
    {
        var client = factory.Server.CreateWebSocketClient();

        return await client.ConnectAsync(new Uri(factory.Server.BaseAddress, "ws/orders"), Cancellation);
    }

    private async Task<string> CreateOrderAsync(string description)
    {
        var response = await factory.Client.PostAsJsonAsync(
            new Uri("/api/orders", UriKind.Relative),
            new CreateOrderRequest(description),
            OrderTrackingApiFactory.Json,
            Cancellation);

        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<OrderDetailsResponse>(
            OrderTrackingApiFactory.Json, Cancellation);

        return created!.OrderNumber;
    }

    private async Task ChangeStatusAsync(string orderNumber, OrderStatus status)
    {
        var response = await factory.Client.PatchAsJsonAsync(
            new Uri($"/api/orders/{orderNumber}/status", UriKind.Relative),
            new ChangeOrderStatusRequest(status),
            OrderTrackingApiFactory.Json,
            Cancellation);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Hands an event to the handler the consumer would have called.</summary>
    private async Task PublishEventAsync(string orderNumber, OrderStatus status, OrderStatus? previous)
    {
        await using var scope = factory.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IOrderEventHandler>();

        await handler.HandleAsync(
            new OrderEventNotification(Guid.NewGuid(), orderNumber, status, previous, DateTimeOffset.UtcNow),
            Cancellation);
    }

    /// <summary>
    /// Reads frames until one of the wanted type arrives.
    /// </summary>
    /// <remarks>
    /// Filtering by type rather than taking the next frame, so that a heartbeat landing
    /// mid-test cannot fail an assertion that has nothing to do with heartbeats.
    /// </remarks>
    private static async Task<JsonElement> ReceiveAsync(WebSocket socket, string type)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        while (true)
        {
            var frame = await ReadFrameAsync(socket, timeout.Token);

            if (frame.GetProperty("type").GetString() == type)
            {
                return frame;
            }
        }
    }

    private static async Task<JsonElement> ReadFrameAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        message.Position = 0;

        using var document = await JsonDocument.ParseAsync(message, cancellationToken: cancellationToken);

        // Cloned because the element does not outlive the document it was parsed from.
        return document.RootElement.Clone();
    }
}

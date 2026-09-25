using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderTracking.Domain.Orders;
using OrderTracking.Infrastructure.Persistence;

namespace OrderTracking.IntegrationTests.Messaging;

/// <summary>
/// Follows an order event from <c>SaveChangesAsync</c> to the consumer, through a real
/// PostgreSQL and a real RabbitMQ.
/// </summary>
public sealed class OrderEventPipelineTests(MessagingFixture fixture) : IClassFixture<MessagingFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creating_and_shipping_an_order_reaches_the_consumer()
    {
        var orderNumber = await CreateOrderAsync("Заказ для сквозной проверки");

        var created = await fixture.Handler.WaitForAsync(
            notification => notification.OrderNumber == orderNumber
                            && notification.PreviousStatus is null,
            Cancellation);

        Assert.Equal(OrderStatus.Created, created.Status);

        await ChangeStatusAsync(orderNumber, OrderStatus.Shipped);

        var shipped = await fixture.Handler.WaitForAsync(
            notification => notification.OrderNumber == orderNumber
                            && notification.Status == OrderStatus.Shipped,
            Cancellation);

        // The event carries where the order came from, not just where it ended up, which is
        // what lets a subscriber render "отправлен" as a transition rather than a fact.
        Assert.Equal(OrderStatus.Created, shipped.PreviousStatus);
        Assert.NotEqual(created.EventId, shipped.EventId);
    }

    [Fact]
    public async Task Outbox_rows_are_marked_processed_and_deduplication_markers_are_written()
    {
        var orderNumber = await CreateOrderAsync("Заказ для проверки outbox");

        var created = await fixture.Handler.WaitForAsync(
            notification => notification.OrderNumber == orderNumber, Cancellation);

        await WaitUntilAsync(async dbContext =>
            !await dbContext.OutboxMessages.AnyAsync(
                message => message.MessageId == created.EventId && message.ProcessedAt == null,
                Cancellation));

        await using var scope = fixture.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();

        var outbox = await context.OutboxMessages
            .SingleAsync(message => message.MessageId == created.EventId, Cancellation);

        Assert.NotNull(outbox.ProcessedAt);
        Assert.Null(outbox.LastError);
        Assert.Equal(0, outbox.AttemptCount);
        Assert.Equal(nameof(Domain.Orders.Events.OrderCreatedEvent), outbox.Type);

        Assert.True(await context.ProcessedMessages
            .AnyAsync(message => message.MessageId == created.EventId, Cancellation));
    }

    [Fact]
    public async Task Redelivering_a_message_does_not_handle_it_twice()
    {
        var orderNumber = await CreateOrderAsync("Заказ для проверки идемпотентности");

        var created = await fixture.Handler.WaitForAsync(
            notification => notification.OrderNumber == orderNumber, Cancellation);

        Assert.Equal(1, fixture.Handler.CountFor(orderNumber));

        await using (var scope = fixture.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();

            await dbContext.Database.ExecuteSqlAsync(
                $"UPDATE outbox_messages SET processed_at = NULL WHERE message_id = {created.EventId}",
                Cancellation);
        }

        // waits for the republish instead of a fixed delay; the consumer is then a few milliseconds behind
        await WaitUntilAsync(async dbContext =>
            !await dbContext.OutboxMessages.AnyAsync(
                message => message.MessageId == created.EventId && message.ProcessedAt == null,
                Cancellation));

        Assert.Equal(1, fixture.Handler.CountFor(orderNumber));
    }

    private async Task<string> CreateOrderAsync(string description)
    {
        await using var scope = fixture.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();
        var generator = scope.ServiceProvider.GetRequiredService<IOrderNumberGenerator>();

        var orderNumber = await generator.NextAsync(Cancellation);

        dbContext.Orders.Add(Order.Create(orderNumber, description, DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(Cancellation);

        return orderNumber;
    }

    private async Task ChangeStatusAsync(string orderNumber, OrderStatus status)
    {
        await using var scope = fixture.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();

        var order = await dbContext.Orders
            .SingleAsync(candidate => candidate.OrderNumber == orderNumber, Cancellation);

        order.ChangeStatus(status, DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(Cancellation);
    }

    private async Task WaitUntilAsync(Func<OrderTrackingDbContext, Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = fixture.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();

            if (await condition(dbContext))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), Cancellation);
        }

        Assert.Fail("The database did not reach the expected state in time.");
    }
}

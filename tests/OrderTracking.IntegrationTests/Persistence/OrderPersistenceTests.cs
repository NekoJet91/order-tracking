using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderTracking.Domain.Orders;
using OrderTracking.Infrastructure.Persistence;

namespace OrderTracking.IntegrationTests.Persistence;

/// <summary>
/// Exercises the mappings that cannot be verified without a real PostgreSQL server.
/// </summary>
public sealed class OrderPersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{

    private const string RoundTripDescription = "Ноутбук Lenovo ThinkPad E14 — 2 шт.";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Order_number_generator_uses_the_sequence()
    {
        await using var scope = fixture.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<IOrderNumberGenerator>();

        var first = await generator.NextAsync(Cancellation);
        var second = await generator.NextAsync(Cancellation);

        Assert.Matches(@"^ORD-\d{8}$", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Order_round_trips_through_the_database()
    {
        var createdAt = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        string orderNumber;

        await using (var scope = fixture.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();
            var generator = scope.ServiceProvider.GetRequiredService<IOrderNumberGenerator>();

            orderNumber = await generator.NextAsync(Cancellation);
            dbContext.Orders.Add(Order.Create(orderNumber, RoundTripDescription, createdAt));
            await dbContext.SaveChangesAsync(Cancellation);
        }

        await using (var scope = fixture.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();

            var loaded = await dbContext.Orders.SingleAsync(
                o => o.OrderNumber == orderNumber, Cancellation);

            Assert.True(loaded.Id > 0);
            Assert.Equal(RoundTripDescription, loaded.Description);
            Assert.Equal(OrderStatus.Created, loaded.Status);
            Assert.Equal(createdAt, loaded.CreatedAt);
        }
    }

    [Fact]
    public async Task Status_is_stored_as_readable_text()
    {
        string orderNumber;

        await using (var scope = fixture.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();
            var generator = scope.ServiceProvider.GetRequiredService<IOrderNumberGenerator>();

            orderNumber = await generator.NextAsync(Cancellation);
            var order = Order.Create(
                orderNumber, "Order for the status-text check", DateTimeOffset.UtcNow);
            order.ChangeStatus(OrderStatus.Shipped, DateTimeOffset.UtcNow);

            dbContext.Orders.Add(order);
            await dbContext.SaveChangesAsync(Cancellation);
        }

        await using (var scope = fixture.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();

            // Reading the raw column rather than the mapped property: the point of the
            // conversion is that someone querying in psql sees "Shipped" and not "2".
            var raw = await dbContext.Database
                .SqlQueryRaw<string>(
                    """SELECT status AS "Value" FROM orders WHERE order_number = {0}""",
                    orderNumber)
                .SingleAsync(Cancellation);

            Assert.Equal("Shipped", raw);
        }
    }

    [Fact]
    public async Task Concurrent_status_changes_are_detected_by_the_xmin_token()
    {
        string orderNumber;

        await using (var setup = fixture.CreateScope())
        {
            var dbContext = setup.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();
            var generator = setup.ServiceProvider.GetRequiredService<IOrderNumberGenerator>();

            orderNumber = await generator.NextAsync(Cancellation);
            dbContext.Orders.Add(Order.Create(
                orderNumber, "Order for the concurrency check", DateTimeOffset.UtcNow));
            await dbContext.SaveChangesAsync(Cancellation);
        }

        // Two independent contexts load the same row, mirroring two concurrent requests.
        await using var scopeA = fixture.CreateScope();
        await using var scopeB = fixture.CreateScope();

        var contextA = scopeA.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();
        var contextB = scopeB.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();

        var orderA = await contextA.Orders.SingleAsync(
            o => o.OrderNumber == orderNumber, Cancellation);
        var orderB = await contextB.Orders.SingleAsync(
            o => o.OrderNumber == orderNumber, Cancellation);

        orderA.ChangeStatus(OrderStatus.Shipped, DateTimeOffset.UtcNow);
        await contextA.SaveChangesAsync(Cancellation);

        // B still holds the xmin it read before A committed, so its UPDATE matches no row.
        orderB.ChangeStatus(OrderStatus.Cancelled, DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => contextB.SaveChangesAsync(Cancellation));
    }

    [Fact]
    public async Task Order_numbers_are_unique()
    {
        await using var scope = fixture.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();
        var generator = scope.ServiceProvider.GetRequiredService<IOrderNumberGenerator>();

        var orderNumber = await generator.NextAsync(Cancellation);

        dbContext.Orders.Add(Order.Create(orderNumber, "Original order", DateTimeOffset.UtcNow));
        dbContext.Orders.Add(Order.Create(
            orderNumber, "Order reusing an existing number", DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync(Cancellation));
    }
}

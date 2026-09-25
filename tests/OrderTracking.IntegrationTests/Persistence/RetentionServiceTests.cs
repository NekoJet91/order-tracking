using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderTracking.Infrastructure.Messaging;
using OrderTracking.Infrastructure.Outbox;
using OrderTracking.Infrastructure.Persistence;

namespace OrderTracking.IntegrationTests.Persistence;

/// <summary>
/// What the retention sweep deletes, and — more importantly — what it refuses to.
/// </summary>
public sealed class RetentionServiceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task It_removes_published_rows_past_their_period()
    {
        var dbContext = await GivenEmptyDatabaseAsync();

        dbContext.OutboxMessages.Add(Published(_now.AddDays(-30)));
        dbContext.OutboxMessages.Add(Published(_now.AddHours(-1)));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        await SweepAsync();

        var remaining = await dbContext.OutboxMessages.AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(remaining);
        Assert.True(remaining[0].ProcessedAt > _now.AddDays(-1));
    }

    [Fact]
    public async Task It_keeps_an_unpublished_row_however_old_it_is()
    {
        var dbContext = await GivenEmptyDatabaseAsync();

        // An event the system promised to deliver and never did. Deleting it would erase the
        // only evidence that the promise was broken.
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            MessageId = Guid.NewGuid(),
            Type = "OrderCreatedEvent",
            Payload = "{}",
            OccurredAt = _now.AddYears(-1),
            ProcessedAt = null,
            AttemptCount = 5,
            LastError = "broker unreachable"
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        await SweepAsync();

        Assert.Equal(1, await dbContext.OutboxMessages
            .CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task It_deletes_more_rows_than_fit_in_one_batch()
    {
        var dbContext = await GivenEmptyDatabaseAsync();

        for (var i = 0; i < 25; i++)
        {
            dbContext.OutboxMessages.Add(Published(_now.AddDays(-30)));
        }

        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A batch size well below the row count, so the loop has to run several times. This
        // is the part that would silently do a fraction of the work if the batching were wrong.
        await SweepAsync(batchSize: 4);

        Assert.Equal(0, await dbContext.OutboxMessages
            .CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task It_removes_expired_deduplication_markers()
    {
        var dbContext = await GivenEmptyDatabaseAsync();

        dbContext.ProcessedMessages.Add(new ProcessedMessage
        {
            MessageId = Guid.NewGuid(),
            ProcessedAt = _now.AddDays(-30)
        });
        dbContext.ProcessedMessages.Add(new ProcessedMessage
        {
            MessageId = Guid.NewGuid(),
            ProcessedAt = _now.AddDays(-1)
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        await SweepAsync();

        Assert.Equal(1, await dbContext.ProcessedMessages
            .CountAsync(TestContext.Current.CancellationToken));
    }

    private async Task<OrderTrackingDbContext> GivenEmptyDatabaseAsync()
    {
        var dbContext = fixture.ScopeFactory.CreateScope()
            .ServiceProvider.GetRequiredService<OrderTrackingDbContext>();

        await TestDatabase.ResetAsync(dbContext, TestContext.Current.CancellationToken);

        return dbContext;
    }

    private static OutboxMessage Published(DateTimeOffset processedAt) => new()
    {
        MessageId = Guid.NewGuid(),
        Type = "OrderStatusChangedEvent",
        Payload = "{}",
        OccurredAt = processedAt.AddSeconds(-1),
        ProcessedAt = processedAt
    };

    private async Task SweepAsync(int batchSize = 1000)
    {
        var options = Options.Create(new RetentionOptions
        {
            OutboxPeriod = TimeSpan.FromDays(7),
            ProcessedMessagePeriod = TimeSpan.FromDays(7),
            BatchSize = batchSize
        });

        var service = new RetentionService(
            fixture.ScopeFactory,
            TimeProvider.System,
            options,
            NullLogger<RetentionService>.Instance);

        await service.SweepAsync(TestContext.Current.CancellationToken);
    }
}

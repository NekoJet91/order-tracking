using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderTracking.Infrastructure.Diagnostics;
using OrderTracking.Infrastructure.Messaging;
using OrderTracking.Infrastructure.Persistence;

namespace OrderTracking.Infrastructure.Outbox;

/// <summary>
/// Drains pending outbox rows to the broker.
/// </summary>
/// <param name="scopeFactory">Creates a scope per sweep, since this service is a singleton.</param>
/// <param name="publisher">Transport the rows are handed to.</param>
/// <param name="timeProvider">Clock, injected so tests are not forced to wait in real time.</param>
/// <param name="options">Batch size, polling interval and attempt limit.</param>
/// <param name="logger">Receives progress and failures.</param>
public sealed partial class OutboxPublisher(
    IServiceScopeFactory scopeFactory,
    IIntegrationEventPublisher publisher,
    TimeProvider timeProvider,
    IOptions<OutboxOptions> options,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int published;

            try
            {
                published = await PublishPendingAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogSweepFailed(logger, exception);
                published = 0;
            }

            // Only idle when the last sweep came up empty.
            if (published == 0)
            {
                try
                {
                    await Task.Delay(_options.PollingInterval, timeProvider, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<int> PublishPendingAsync(CancellationToken cancellationToken)
    {
        // A BackgroundService is a singleton and DbContext is scoped, so the context cannot
        // be injected: it would be captured for the life of the process, accumulate every
        // entity it ever loaded, and be shared across iterations.
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // FOR UPDATE SKIP LOCKED is what makes more than one instance of the API safe to
        // run. Each sweep claims rows no one else holds and steps over the rest instead of
        // blocking on them.
        var pending = await dbContext.OutboxMessages
            .FromSql($"""
                SELECT *
                FROM   outbox_messages
                WHERE  processed_at IS NULL
                  AND  attempt_count < {_options.MaxAttempts}
                ORDER  BY id
                LIMIT  {_options.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (pending.Count == 0)
        {
            return 0;
        }

        var published = 0;

        foreach (var message in pending)
        {
            try
            {
                await publisher.PublishAsync(message, cancellationToken).ConfigureAwait(false);

                message.ProcessedAt = timeProvider.GetUtcNow();
                message.LastError = null;
                published++;

                // The number to alert on: how long an event waited between happening and
                // reaching the broker. It is the polling interval plus whatever went wrong,
                // and it needs no extra query to measure.
                OrderTrackingDiagnostics.OutboxPublished(
                    message.ProcessedAt.Value - message.OccurredAt);
            }
#pragma warning disable CA1031 // One unpublishable message must not stall the ones behind it.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                message.AttemptCount++;
                message.LastError = exception.Message;

                OrderTrackingDiagnostics.OutboxFailed();

                LogPublishFailed(logger, message.MessageId, message.AttemptCount, exception);

                if (message.AttemptCount >= _options.MaxAttempts)
                {
                    LogAttemptsExhausted(logger, message.MessageId, message.AttemptCount);
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (published > 0)
        {
            LogPublished(logger, published);
        }

        return published;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Published {Count} outbox message(s).")]
    private static partial void LogPublished(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to publish outbox message {MessageId}, attempt {AttemptCount}.")]
    private static partial void LogPublishFailed(
        ILogger logger, Guid messageId, int attemptCount, Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Outbox message {MessageId} has failed {AttemptCount} times and will no longer "
                  + "be retried. It stays pending for inspection rather than being discarded.")]
    private static partial void LogAttemptsExhausted(ILogger logger, Guid messageId, int attemptCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox sweep failed.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}

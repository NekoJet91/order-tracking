using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrderTracking.Domain.Orders.Events;
using OrderTracking.Infrastructure.Diagnostics;

namespace OrderTracking.Infrastructure.Outbox;

/// <summary>
/// Turns recorded domain events into outbox rows as part of the same <c>SaveChanges</c>.
/// </summary>
/// <remarks>
/// <para>
/// Interceptor rather than a service because being in the same transaction has to be
/// impossible to forget. A service could be bypassed by any caller that wrote to the
/// context directly; an interceptor cannot be.
/// </para>
/// </remarks>
internal sealed class DomainEventsToOutboxInterceptor : SaveChangesInterceptor
{
    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        WriteOutboxMessages(eventData.Context);

        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        WriteOutboxMessages(eventData.Context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void WriteOutboxMessages(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var carriers = context.ChangeTracker
            .Entries<IHasDomainEvents>()
            .Where(entry => entry.Entity.DomainEvents.Count > 0)
            .ToArray();

        if (carriers.Length == 0)
        {
            return;
        }

        // Captured once per SaveChanges and replayed onto the broker message later, so a
        // trace can span the HTTP request, the publication and the consumer instead of
        // breaking at the queue.
        var traceParent = Activity.Current?.Id;

        foreach (var carrier in carriers)
        {
            foreach (var domainEvent in carrier.Entity.DomainEvents)
            {
                context.Set<OutboxMessage>().Add(new OutboxMessage
                {
                    MessageId = domainEvent.EventId,
                    Type = domainEvent.GetType().Name,
                    Payload = JsonSerializer.Serialize(
                        domainEvent, domainEvent.GetType(), OutboxSerialization.Options),
                    OccurredAt = domainEvent.OccurredAt,
                    TraceParent = traceParent
                });

                Count(domainEvent);
            }

            // Cleared before the save is known to have succeeded. Safe, because a failed
            // SaveChanges rolls back the outbox rows along with everything else, and the
            // entity instance dies with the request scope rather than being reused.
            carrier.Entity.ClearDomainEvents();
        }
    }

    /// <summary>
    /// Counts the business event behind the row.
    /// </summary>
    /// <remarks>
    /// Here rather than in the endpoints, because this is the one place every change passes
    /// through — the REST API, the status simulator and anything added later included.
    /// </remarks>
    private static void Count(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case OrderCreatedEvent:
                OrderTrackingDiagnostics.OrderCreated();
                break;
            case OrderStatusChangedEvent changed:
                OrderTrackingDiagnostics.StatusChanged(changed.OldStatus, changed.NewStatus);
                break;
            default:
                break;
        }
    }
}

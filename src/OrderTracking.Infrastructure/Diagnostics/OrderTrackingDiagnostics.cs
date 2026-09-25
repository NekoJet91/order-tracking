using System.Diagnostics;
using System.Diagnostics.Metrics;
using OrderTracking.Domain.Orders;

namespace OrderTracking.Infrastructure.Diagnostics;

/// <summary>
/// The application's own spans and instruments.
/// </summary>
/// <remarks>
/// <para>
/// Built on <see cref="ActivitySource"/> and <see cref="Meter"/> from the base class library,
/// with no reference to any OpenTelemetry package. Instrumented code therefore does not know
/// who is listening, and choosing an exporter stays a decision the host makes — see
/// <c>ObservabilityExtensions</c> in the API project, the only place OpenTelemetry appears.
/// </para>
/// <para>
/// Nothing here allocates while unobserved: an <see cref="ActivitySource"/> with no listener
/// returns <c>null</c> from <c>StartActivity</c>, and a <see cref="Counter{T}"/> with no
/// collector discards the measurement.
/// </para>
/// </remarks>
public static class OrderTrackingDiagnostics
{
    /// <summary>Name to register with a tracer provider.</summary>
    public const string ActivitySourceName = "OrderTracking";

    /// <summary>Name to register with a meter provider.</summary>
    public const string MeterName = "OrderTracking";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);

    private static readonly Counter<long> _ordersCreated = _meter.CreateCounter<long>(
        "ordertracking.orders.created", unit: "{order}", description: "Orders placed.");

    private static readonly Counter<long> _statusChanges = _meter.CreateCounter<long>(
        "ordertracking.orders.status_changed", unit: "{change}",
        description: "Status transitions, tagged with where they went from and to.");

    private static readonly Counter<long> _outboxPublished = _meter.CreateCounter<long>(
        "ordertracking.outbox.published", unit: "{message}",
        description: "Outbox rows confirmed by the broker.");

    private static readonly Counter<long> _outboxFailures = _meter.CreateCounter<long>(
        "ordertracking.outbox.failures", unit: "{message}",
        description: "Publication attempts that threw.");

    private static readonly Histogram<double> _outboxLag = _meter.CreateHistogram<double>(
        "ordertracking.outbox.lag", unit: "s",
        description: "Seconds between a domain event occurring and the broker confirming it.");

    private static readonly Counter<long> _messagesHandled = _meter.CreateCounter<long>(
        "ordertracking.consumer.handled", unit: "{message}",
        description: "Broker messages processed, tagged with the outcome.");

    private static readonly UpDownCounter<long> _socketConnections = _meter.CreateUpDownCounter<long>(
        "ordertracking.socket.connections", unit: "{connection}",
        description: "WebSocket clients currently attached.");

    private static readonly Counter<long> _framesSent = _meter.CreateCounter<long>(
        "ordertracking.socket.frames_sent", unit: "{frame}",
        description: "Frames written to clients, tagged with the frame type.");

    private static readonly Counter<long> _retentionDeleted = _meter.CreateCounter<long>(
        "ordertracking.retention.deleted", unit: "{row}",
        description: "Rows removed by the retention sweep, tagged with the table.");

    /// <summary>Starts a span, or returns <c>null</c> when nothing is listening.</summary>
    /// <param name="name">Span name.</param>
    /// <param name="kind">Span kind.</param>
    /// <param name="parent">Parent context, for spans that continue a trace across a queue.</param>
    /// <returns>The started activity, or <c>null</c>.</returns>
    public static Activity? StartActivity(
        string name,
        ActivityKind kind = ActivityKind.Internal,
        ActivityContext parent = default) =>
        _activitySource.StartActivity(name, kind, parent);

    /// <summary>Records that an order was placed.</summary>
    public static void OrderCreated() => _ordersCreated.Add(1);

    /// <summary>Records a status transition.</summary>
    /// <param name="from">Status the order left.</param>
    /// <param name="to">Status the order entered.</param>
    public static void StatusChanged(OrderStatus from, OrderStatus to) =>
        _statusChanges.Add(1,
            new KeyValuePair<string, object?>("from", from.ToString()),
            new KeyValuePair<string, object?>("to", to.ToString()));

    /// <summary>Records a confirmed publication and how long the row waited.</summary>
    /// <param name="lag">Time between the event occurring and the broker confirming it.</param>
    public static void OutboxPublished(TimeSpan lag)
    {
        _outboxPublished.Add(1);
        _outboxLag.Record(lag.TotalSeconds);
    }

    /// <summary>Records a publication attempt that threw.</summary>
    public static void OutboxFailed() => _outboxFailures.Add(1);

    /// <summary>Records a message leaving the consumer.</summary>
    /// <param name="outcome">One of <c>handled</c>, <c>duplicate</c>, <c>unreadable</c>, <c>failed</c>.</param>
    public static void MessageHandled(string outcome) =>
        _messagesHandled.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Records a client attaching or detaching.</summary>
    /// <param name="delta"><c>1</c> on connect, <c>-1</c> on disconnect.</param>
    public static void SocketConnections(int delta) => _socketConnections.Add(delta);

    /// <summary>Records frames written to clients.</summary>
    /// <param name="frameType">The frame's discriminator.</param>
    /// <param name="count">How many clients received it.</param>
    public static void FramesSent(string frameType, int count)
    {
        if (count > 0)
        {
            _framesSent.Add(count, new KeyValuePair<string, object?>("type", frameType));
        }
    }

    /// <summary>Records rows removed by the retention sweep.</summary>
    /// <param name="table">Table the rows came from.</param>
    /// <param name="count">How many were deleted.</param>
    public static void RetentionDeleted(string table, int count)
    {
        if (count > 0)
        {
            _retentionDeleted.Add(count, new KeyValuePair<string, object?>("table", table));
        }
    }
}

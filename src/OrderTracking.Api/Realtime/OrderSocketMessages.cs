using System.Text.Json.Serialization;
using OrderTracking.Api.Contracts.Orders;

namespace OrderTracking.Api.Realtime;

/// <summary>
/// The first frame on a new connection: every order the client should start from.
/// </summary>
/// <param name="Orders">Current orders, newest first.</param>
/// <param name="Counts">
/// How many orders are in each status, across the whole table rather than only the orders
/// in this frame.
/// </param>
/// <remarks>
/// <para>
/// Sent instead of making the client fetch the list over HTTP and then subscribe. Those two
/// steps have a gap between them, and a status that changes inside the gap is missed by
/// both — the list was read too early and the subscription started too late.
/// </para>
/// <para>
/// Bounded to the most recent orders rather than the whole table: a snapshot is a starting
/// point for a screen, not a replication mechanism. <paramref name="Counts"/> is here because
/// of that bound — a client counting what it holds would be counting a page and presenting
/// the result as a total.
/// </para>
/// </remarks>
public sealed record OrderSnapshotMessage(
    IReadOnlyList<OrderSummaryResponse> Orders,
    IReadOnlyDictionary<string, int> Counts)
{
    /// <summary>Discriminator, always <c>snapshot</c>.</summary>
    [JsonPropertyOrder(-1)]
    public string Type => "snapshot";
}

/// <summary>
/// One order has changed, or has just been created.
/// </summary>
/// <param name="Order">The order as it now stands.</param>
/// <param name="Counts">The per-status totals after this change.</param>
/// <remarks>
/// <para>
/// Carries the whole order rather than just the new status, which makes creation and change
/// the same frame and lets a client that has never seen this order render it immediately.
/// </para>
/// <para>
/// The client must treat these as unordered and ignore an order whose <c>updatedAt</c> is
/// not newer than the copy it holds. Two things make that necessary: a delta queued before
/// the snapshot was read can be delivered after it, and the broker guarantees at-least-once
/// rather than exactly-once delivery. Comparing a version is what turns both into
/// non-events.
/// </para>
/// <para>
/// <paramref name="Counts"/> is sent rather than left for the client to maintain from the
/// transition. A client cannot dedupe the arithmetic the way it dedupes the order, because
/// the reply to its own POST arrives outside this stream: the matching frame then looks like
/// a duplicate and its increment would be dropped.
/// </para>
/// </remarks>
public sealed record OrderChangedMessage(
    OrderSummaryResponse Order,
    IReadOnlyDictionary<string, int> Counts)
{
    /// <summary>Discriminator, always <c>order-changed</c>.</summary>
    [JsonPropertyOrder(-1)]
    public string Type => "order-changed";
}

/// <summary>
/// Proof that the connection is still alive.
/// </summary>
/// <param name="At">Server time when the frame was produced.</param>
/// <remarks>
/// <para>
/// An application-level heartbeat on top of the protocol-level ping frames the server
/// already sends. Browsers answer pings in the network stack and never surface them to
/// JavaScript, so a page cannot tell a quiet connection from a dead one without this. The
/// client is expected to reconnect when heartbeats stop arriving.
/// </para>
/// </remarks>
public sealed record HeartbeatMessage(DateTimeOffset At)
{
    /// <summary>Discriminator, always <c>heartbeat</c>.</summary>
    [JsonPropertyOrder(-1)]
    public string Type => "heartbeat";
}

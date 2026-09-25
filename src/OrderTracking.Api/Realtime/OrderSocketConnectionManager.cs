using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using OrderTracking.Api.Contracts.Orders;
using OrderTracking.Infrastructure.Diagnostics;

namespace OrderTracking.Api.Realtime;

/// <summary>
/// Keeps track of the connected clients and fans frames out to them.
/// </summary>
/// <param name="jsonOptions">Serialisation settings shared with the REST endpoints.</param>
/// <param name="socketOptions">Socket tuning.</param>
/// <param name="logger">Receives connection churn and slow-client disconnects.</param>
/// <remarks>
/// <para>
/// A singleton holding process-local state, which is the whole reason the event consumer
/// runs in this process: these sockets exist only in this application's memory and no other
/// process can write to them.
/// </para>
/// <para>
/// Serialisation settings are taken from the same options the minimal APIs use, so a status
/// arrives over the socket spelled exactly as the REST API spells it — otherwise a client
/// ends up parsing <c>"Shipped"</c> in one place and <c>2</c> in the other.
/// </para>
/// </remarks>
public sealed partial class OrderSocketConnectionManager(
    IOptions<JsonOptions> jsonOptions,
    IOptions<OrderSocketOptions> socketOptions,
    ILogger<OrderSocketConnectionManager> logger)
{
    private readonly ConcurrentDictionary<Guid, OrderSocketConnection> _connections = new();

    private readonly JsonSerializerOptions _json = jsonOptions?.Value.SerializerOptions
        ?? throw new ArgumentNullException(nameof(jsonOptions));

    private readonly OrderSocketOptions _options = socketOptions?.Value
        ?? throw new ArgumentNullException(nameof(socketOptions));

    /// <summary>How many clients are connected right now.</summary>
    public int Count => _connections.Count;

    /// <summary>Serializes a message the way this socket sends it.</summary>
    /// <typeparam name="TMessage">The concrete message type.</typeparam>
    /// <param name="message">The message to serialize.</param>
    /// <returns>UTF-8 JSON.</returns>
    /// <remarks>
    /// Generic on the concrete type rather than taking a base class, because serializing
    /// through a base reference would write only the base's properties.
    /// </remarks>
    public byte[] Serialize<TMessage>(TMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, _json);

    /// <summary>Starts tracking a connection.</summary>
    /// <param name="connection">The connection to track.</param>
    internal void Add(OrderSocketConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connections[connection.Id] = connection;
        OrderTrackingDiagnostics.SocketConnections(1);
        LogConnected(logger, connection.Id, _connections.Count);
    }

    /// <summary>Stops tracking a connection.</summary>
    /// <param name="connection">The connection to forget.</param>
    internal void Remove(OrderSocketConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Guarded so a second call for the same connection cannot drive the gauge negative.
        if (_connections.TryRemove(connection.Id, out _))
        {
            OrderTrackingDiagnostics.SocketConnections(-1);
            LogDisconnected(logger, connection.Id, _connections.Count);
        }
    }

    /// <summary>Sends one order to every connected client.</summary>
    /// <param name="order">The order as it now stands.</param>
    /// <param name="counts">The per-status totals after the change.</param>
    public void Broadcast(OrderSummaryResponse order, IReadOnlyDictionary<string, int> counts)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(counts);

        Fan(Serialize(new OrderChangedMessage(order, counts)), "order-changed");
    }

    /// <summary>Sends a heartbeat to every connected client.</summary>
    /// <param name="at">Server time to stamp the frame with.</param>
    public void Heartbeat(DateTimeOffset at) => Fan(Serialize(new HeartbeatMessage(at)), "heartbeat");

    private void Fan(ReadOnlyMemory<byte> frame, string frameType)
    {
        var delivered = 0;

        // Serialised once above and shared by every connection: the frame is identical for
        // all of them, and the alternative is the same work repeated per client.
        foreach (var connection in _connections.Values)
        {
            if (connection.TryEnqueue(frame))
            {
                delivered++;
                continue;
            }

            // The queue filled up, so this client is not reading.
            LogSlowClient(logger, connection.Id, _options.SendQueueCapacity);
            _ = connection.CloseAsync();
        }

        OrderTrackingDiagnostics.FramesSent(frameType, delivered);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Socket {ConnectionId} connected ({Total} open).")]
    private static partial void LogConnected(ILogger logger, Guid connectionId, int total);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Socket {ConnectionId} disconnected ({Total} open).")]
    private static partial void LogDisconnected(ILogger logger, Guid connectionId, int total);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Socket {ConnectionId} fell more than {Capacity} frames behind and was closed.")]
    private static partial void LogSlowClient(ILogger logger, Guid connectionId, int capacity);
}

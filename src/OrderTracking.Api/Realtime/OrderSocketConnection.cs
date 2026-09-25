using System.Net.WebSockets;
using System.Threading.Channels;

namespace OrderTracking.Api.Realtime;

/// <summary>
/// One connected client: the socket, its outbound queue, and the loops that drive them.
/// </summary>
/// <remarks>
/// <para>
/// Frames are queued rather than written by whoever produced them. A broadcast fans out to
/// every client, so writing inline would make the slowest client set the pace for all of
/// them — and <see cref="WebSocket.SendAsync(ArraySegment{byte}, WebSocketMessageType, bool,
/// CancellationToken)"/> may not be called concurrently on one socket anyway, so some form
/// of serialisation is required regardless.
/// </para>
/// <para>
/// The queue is bounded, and a client that fills it is disconnected rather than having its
/// frames dropped. Dropping leaves a screen quietly showing the wrong thing; disconnecting is
/// noisy but self-healing, because the client reconnects onto a fresh snapshot. Unbounded
/// would turn a slow reader into a memory leak.
/// </para>
/// </remarks>
internal sealed class OrderSocketConnection : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly Channel<ReadOnlyMemory<byte>> _outbound;
    private readonly CancellationTokenSource _closing = new();

    /// <summary>Creates a connection around an accepted socket.</summary>
    /// <param name="socket">The accepted socket.</param>
    /// <param name="queueCapacity">How many frames may be waiting before the client is dropped.</param>
    public OrderSocketConnection(WebSocket socket, int queueCapacity)
    {
        _socket = socket;

        _outbound = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(queueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
    }

    /// <summary>Identifies the connection in logs and in the manager's table.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>
    /// Queues one already-serialized frame.
    /// </summary>
    /// <param name="frame">UTF-8 JSON to send.</param>
    /// <returns><c>false</c> when the queue is full, meaning the client cannot keep up.</returns>
    /// <remarks>
    /// Takes bytes rather than an object so a broadcast serializes once and every connection
    /// shares the result.
    /// </remarks>
    public bool TryEnqueue(ReadOnlyMemory<byte> frame) => _outbound.Writer.TryWrite(frame);

    /// <summary>Sends one frame immediately, outside the queue.</summary>
    /// <param name="frame">UTF-8 JSON to send.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the frame has been written.</returns>
    /// <remarks>
    /// Used for the snapshot only, and only before <see cref="RunAsync"/> starts, so it
    /// cannot race the pump. It is what guarantees the snapshot is the first frame the
    /// client sees even though deltas may already be queued behind it.
    /// </remarks>
    public ValueTask SendImmediateAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken) =>
        _socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    /// <summary>
    /// Pumps the queue to the socket and watches for the client going away, returning when
    /// either stops.
    /// </summary>
    /// <param name="cancellationToken">Stops the connection, typically at shutdown.</param>
    /// <returns>A task that completes once the connection is finished with.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _closing.Token);

        var send = SendLoopAsync(linked.Token);
        var receive = ReceiveLoopAsync(linked.Token);

        // Whichever finishes first ends the connection: a closed socket makes sending
        // pointless, and a failed send makes listening pointless.
        await Task.WhenAny(send, receive);

        await _closing.CancelAsync();
        _outbound.Writer.TryComplete();

        // Observed rather than abandoned, so a failure in the loop that did not win the race
        // is not left as an unobserved task exception.
        await Task.WhenAll(Swallow(send), Swallow(receive));
    }

    /// <summary>Asks the connection to stop.</summary>
    /// <returns>A task that completes once cancellation has been signaled.</returns>
    public Task CloseAsync() => _closing.CancelAsync();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure, statusDescription: null, CancellationToken.None);
            }
#pragma warning disable CA1031 // Closing is best effort; the client may already be gone.
            catch (Exception)
#pragma warning restore CA1031
            {
                // We are already tearing the connection down.
            }
        }

        _socket.Dispose();
        _closing.Dispose();
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var frame in _outbound.Reader.ReadAllAsync(cancellationToken))
        {
            await _socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        // The client has nothing to say — it reads status changes and posts commands over
        // HTTP. Receiving is still mandatory: without a pending read the server never
        // observes a close frame, and a browser tab that went away would leave a connection
        // and its queue alive until a send happened to fail.
        var buffer = new byte[256];

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken);

            if (result.MessageType is WebSocketMessageType.Close)
            {
                return;
            }
        }
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task;
        }
#pragma warning disable CA1031 // The connection is ending; the reason has already been acted on.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Expected on a client that disappears without a close handshake.
        }
    }
}

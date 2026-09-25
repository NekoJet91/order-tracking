using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace OrderTracking.Infrastructure.Messaging;

/// <summary>
/// Owns the single AMQP connection shared by the publisher and the consumer.
/// </summary>
/// <param name="options">Broker settings.</param>
/// <remarks>
/// <para>
/// One connection per process, many channels on it. A TCP connection with its AMQP
/// handshake is expensive and the broker has a hard limit on them; channels are cheap and
/// exist precisely so that one connection can carry many independent conversations.
/// </para>
/// <para>
/// Connecting is deferred rather than done in the constructor. A DI container builds
/// singletons eagerly and synchronously, so connecting there would either block a thread
/// or make the whole application fail to start because a broker was slow.
/// </para>
/// </remarks>
public sealed class RabbitMqConnection(IOptions<RabbitMqOptions> options) : IAsyncDisposable
{
    private readonly RabbitMqOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    private readonly SemaphoreSlim _gate = new(1, 1);

    private IConnection? _connection;

    /// <summary>Returns the open connection, establishing it on first use.</summary>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>A connection that is open at the moment it is returned.</returns>
    public async Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-checked inside the gate: several background services race for the
            // connection on startup and only one of them should create it.
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }

            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,
                // The client reconnects and redeclares topology by itself, which turns a
                // broker restart into a pause rather than an outage.
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);

            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        _gate.Dispose();
    }
}

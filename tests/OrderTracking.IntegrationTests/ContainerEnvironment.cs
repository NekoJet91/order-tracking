using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace OrderTracking.IntegrationTests;

/// <summary>
/// Starts a PostgreSQL server and a RabbitMQ broker for the whole test run.
/// </summary>
/// <remarks>
/// An assembly fixture, so the containers start once. Per class would be correct and unusably
/// slow: a fresh database and a fresh set of queue names give the same isolation for
/// milliseconds instead of seconds. Images are pinned to the versions
/// <c>docker-compose.yml</c> runs, so the tests exercise the servers the application is
/// deployed against.
/// </remarks>
public sealed class ContainerEnvironment : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:14-alpine")
        .WithDatabase("postgres")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4-alpine").Build();

    /// <summary>Where the broker is listening, including credentials.</summary>
    public Uri AmqpUri { get; private set; } = null!;

    /// <summary>
    /// Creates an empty database on the shared server and returns a connection string for it.
    /// </summary>
    /// <param name="purpose">A short word identifying the caller; it ends up in the name.</param>
    /// <returns>A connection string to a database nothing else will touch.</returns>
    /// <remarks>
    /// One database per fixture is what allows the classes to run in parallel: each migrates
    /// and truncates its own schema, so no test can clear another's rows.
    /// </remarks>
    public async Task<string> CreateDatabaseAsync(string purpose)
    {
        var name = $"ordertracking_{purpose}_{Guid.NewGuid():N}"[..40];

        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // Interpolated because CREATE DATABASE takes no parameters; the name is a literal
        // plus a GUID, so there is nothing to inject.
        await using var command = new NpgsqlCommand($"""CREATE DATABASE "{name}" """, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        return new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Database = name
        }.ConnectionString;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        // Concurrently: neither depends on the other, and pulling images is the slow part.
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

        AmqpUri = new Uri(_rabbitMq.GetConnectionString());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _rabbitMq.DisposeAsync();
    }
}

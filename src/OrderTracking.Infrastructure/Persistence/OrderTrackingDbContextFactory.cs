using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OrderTracking.Infrastructure.Persistence;

/// <summary>
/// Builds a context for the <c>dotnet ef</c> tooling at design time.
/// </summary>
/// <remarks>
/// <para>
/// Without this, the tooling starts the API host to obtain a context, which means design-time
/// commands depend on the application's whole configuration being present — including a
/// connection string the host refuses to start without. This factory decouples the two.
/// </para>
/// <para>
/// Scaffolding a migration does not connect to a database, so the default below has no
/// password and is only a shape for the provider to reason about. Applying a migration does
/// connect: set <c>ORDERTRACKING_MIGRATIONS_CONNECTION</c> for that.
/// </para>
/// <para>
/// When a design-time factory exists the tooling uses it and does <em>not</em> consult the
/// application's configuration, so the API's user-secrets are not in play here.
/// </para>
/// </remarks>
public sealed class OrderTrackingDbContextFactory : IDesignTimeDbContextFactory<OrderTrackingDbContext>
{
    private const string _connectionEnvironmentVariable = "ORDERTRACKING_MIGRATIONS_CONNECTION";

    private const string _defaultDesignTimeConnection =
        "Host=localhost;Port=5432;Database=ordertracking_dev;Username=postgres";

    /// <inheritdoc />
    public OrderTrackingDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(_connectionEnvironmentVariable)
            ?? _defaultDesignTimeConnection;

        var options = new DbContextOptionsBuilder<OrderTrackingDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new OrderTrackingDbContext(options);
    }
}

using Microsoft.Extensions.DependencyInjection;
using OrderTracking.Infrastructure;
using OrderTracking.Infrastructure.Persistence;

namespace OrderTracking.IntegrationTests.Persistence;

/// <summary>
/// Brings up a migrated PostgreSQL schema once per test class, without an HTTP host.
/// </summary>
/// <remarks>
/// Deliberately does not boot the application. These tests are about what EF Core and
/// PostgreSQL do with the model, and a web host in the middle would only add ways for them
/// to fail for reasons that have nothing to do with the mapping.
/// </remarks>
/// <param name="environment">
/// The assembly-wide containers, injected by xUnit because an assembly fixture is available
/// to the class fixtures that need it.
/// </param>
public sealed class PostgresFixture(ContainerEnvironment environment) : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    /// <summary>Creates a scope holding a context bound to the test database.</summary>
    /// <returns>A new scope; dispose it to dispose the context.</returns>
    public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    /// <summary>The scope factory, for services that create their own scope per unit of work.</summary>
    public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var connectionString = await environment.CreateDatabaseAsync("persistence");

        var services = new ServiceCollection();
        services.AddOrderTrackingInfrastructure(connectionString);
        _provider = services.BuildServiceProvider();

        await using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();

        await TestDatabase.ResetAsync(dbContext, TestContext.Current.CancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OrderTracking.Infrastructure.Messaging;
using OrderTracking.Infrastructure.Outbox;
using OrderTracking.Infrastructure.Persistence;

namespace OrderTracking.IntegrationTests.Api;

/// <summary>
/// Boots the real application in memory and points it at the test database.
/// </summary>
/// <remarks>
/// Nothing is substituted: the same endpoint filters, the same exception handler, the same
/// EF Core provider against a real PostgreSQL. An in-memory provider would stop testing the
/// behaviour only PostgreSQL produces — the concurrency token, the sequence, the unique
/// index.
/// </remarks>
/// <param name="environment">The assembly-wide PostgreSQL container.</param>
public sealed class OrderTrackingApiFactory(ContainerEnvironment environment)
    : WebApplicationFactory<Program>, IAsyncLifetime
{
    private string _connectionString = null!;

    /// <summary>
    /// Serializer settings matching the server's: camelCase members and statuses as names.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>A client bound to the in-memory host.</summary>
    public HttpClient Client { get; private set; } = null!;

    /// <summary>Creates a scope on the running host's container, for inspecting the database.</summary>
    /// <returns>A new scope; dispose it to dispose whatever it resolved.</returns>
    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");

        // Same key the application reads in production; only the value differs.
        builder.UseSetting("ConnectionStrings:OrderTracking", _connectionString);

        // The two message-pump services are dropped, and only those two. These tests are
        // about the HTTP surface, so requiring a broker to be running would be gratuitous,
        // and a publisher draining the outbox in the background would race every assertion
        // about what the endpoints wrote. The broker pipeline has its own fixture.
        builder.ConfigureTestServices(services => services
            .RemoveHostedService<OutboxPublisher>()
            .RemoveHostedService<OrderEventsConsumer>());
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        // Before CreateClient, which is what builds the host and runs ConfigureWebHost.
        _connectionString = await environment.CreateDatabaseAsync("api");

        Client = CreateClient();

        // Touching Services after CreateClient means the host is already built, so this
        // resolves the very same container the endpoints will use.
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderTrackingDbContext>();

        await TestDatabase.ResetAsync(dbContext, TestContext.Current.CancellationToken);
    }
}

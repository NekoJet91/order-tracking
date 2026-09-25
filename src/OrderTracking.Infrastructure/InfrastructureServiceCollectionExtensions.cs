using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderTracking.Domain.Orders;
using OrderTracking.Infrastructure.Messaging;
using OrderTracking.Infrastructure.Outbox;
using OrderTracking.Infrastructure.Persistence;

namespace OrderTracking.Infrastructure;

/// <summary>
/// Registration of the infrastructure layer with the dependency injection container.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers the database context and the services that depend on it.
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <param name="connectionString">A Npgsql connection string.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// Takes a connection string rather than <c>IConfiguration</c> so this layer does not
    /// need to know how the host stores its settings, and so tests can supply a
    /// Testcontainers connection string directly.
    /// </remarks>
    public static IServiceCollection AddOrderTrackingInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Stateless, so one instance serves every context.
        services.AddSingleton<DomainEventsToOutboxInterceptor>();

        services.AddDbContext<OrderTrackingDbContext>((provider, options) =>
            options
                .UseNpgsql(connectionString)
                // Produces snake_case tables and columns, so hand-written SQL and psql
                // output need no quoting. Without this, EF generates "OrderNumber" and
                // every ad-hoc query has to remember the double quotes.
                .UseSnakeCaseNamingConvention()
                // Registered on the context rather than called by application code, so that
                // writing an outbox row inside the same transaction is not something any
                // caller can forget to do.
                .AddInterceptors(provider.GetRequiredService<DomainEventsToOutboxInterceptor>()));

        services.AddScoped<IOrderNumberGenerator, SequenceOrderNumberGenerator>();

        return services;
    }

    /// <summary>
    /// Registers the broker transport, the outbox publisher and the event consumer.
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <param name="configureRabbitMq">Applies broker settings.</param>
    /// <param name="configureOutbox">Applies outbox tuning, or <c>null</c> for the defaults.</param>
    /// <param name="configureRetention">Applies retention settings, or <c>null</c> for the defaults.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// Separate from <see cref="AddOrderTrackingInfrastructure"/> so that persistence can be
    /// used without a broker. The persistence tests need a database but have no business
    /// requiring RabbitMQ to be running.
    /// </remarks>
    public static IServiceCollection AddOrderTrackingMessaging(
        this IServiceCollection services,
        Action<RabbitMqOptions> configureRabbitMq,
        Action<OutboxOptions>? configureOutbox = null,
        Action<RetentionOptions>? configureRetention = null)
    {
        ArgumentNullException.ThrowIfNull(configureRabbitMq);

        services.Configure(configureRabbitMq);
        services.Configure(configureOutbox ?? (_ => { }));
        services.Configure(configureRetention ?? (_ => { }));

        services.AddSingleton<RabbitMqConnection>();
        services.AddSingleton<IIntegrationEventPublisher, RabbitMqEventPublisher>();

        // TryAdd, so a host that has its own handler — the API registers the WebSocket
        // broadcaster — keeps it, while anything using this layer on its own still ends the
        // pipeline somewhere observable rather than nowhere.
        services.TryAddScoped<IOrderEventHandler, LoggingOrderEventHandler>();

        services.AddHostedService<OutboxPublisher>();
        services.AddHostedService<OrderEventsConsumer>();

        // Registered here rather than with persistence: one of the two tables it prunes is
        // the deduplication marker table, which only exists because there is a broker.
        services.AddHostedService<RetentionService>();

        return services;
    }
}

using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OrderTracking.Infrastructure.Diagnostics;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace OrderTracking.Api.Observability;

/// <summary>
/// Wires logging, tracing and metrics into the host.
/// </summary>
/// <remarks>
/// The only place OpenTelemetry is named. Everything that produces telemetry does so through
/// <see cref="OrderTrackingDiagnostics"/>, which knows nothing about who collects it.
/// </remarks>
public static class ObservabilityExtensions
{
    private const string ServiceName = "order-tracking-api";

    /// <summary>
    /// Replaces the default logger with Serilog.
    /// </summary>
    /// <param name="builder">The host being configured.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Console output is JSON outside Development, because the thing that reads it there is a
    /// log shipper rather than a person, and a shipper re-parsing a rendered string to recover
    /// the fields that were already structured is work that should not exist. In Development
    /// it is the readable template, because there the reader is a person.
    /// </para>
    /// <para>
    /// The file sink rolls daily and keeps a week. It exists so that a container which has
    /// already restarted still has evidence, and it is bounded so that the evidence cannot
    /// fill the disk.
    /// </para>
    /// </remarks>
    public static WebApplicationBuilder AddOrderTrackingLogging(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var development = builder.Environment.IsDevelopment();

        builder.Host.UseSerilog((context, services, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("service.name", ServiceName);

            // No trace enricher: Serilog reads Activity.Current itself and the compact
            // formatter writes the ids as @tr and @sp. Adding them again as properties would
            // put the same two values twice in every line.
            if (development)
            {
                configuration.WriteTo.Console(
                    outputTemplate:
                    "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
            }
            else
            {
                configuration.WriteTo.Console(new CompactJsonFormatter());
            }

            configuration.WriteTo.File(
                new CompactJsonFormatter(),
                path: Path.Combine(AppContext.BaseDirectory, "logs", "ordertracking-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 64 * 1024 * 1024,
                rollOnFileSizeLimit: true);
        });

        return builder;
    }

    /// <summary>
    /// Registers tracing and metrics.
    /// </summary>
    /// <param name="builder">The host being configured.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Spans are pushed over OTLP; metrics are pulled from <c>/metrics</c>. That split follows
    /// the backends: a trace has to be sent because nobody can guess when it happened, while a
    /// counter is a current value a scraper can ask for whenever it likes.
    /// </para>
    /// <para>
    /// The OTLP exporter is registered only when an endpoint is configured, so running the API
    /// without a collector does not mean failing to reach one every export interval.
    /// </para>
    /// </remarks>
    public static WebApplicationBuilder AddOrderTrackingTelemetry(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The variable the OpenTelemetry SDK reads by convention, checked here as well so the
        // exporter can be left out entirely rather than registered and left to fail.
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

        var resource = ResourceBuilder.CreateDefault()
            .AddService(ServiceName, serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString())
            .AddEnvironmentVariableDetector();

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(resource)
                    .AddSource(OrderTrackingDiagnostics.ActivitySourceName)
                    .AddAspNetCoreInstrumentation(instrumentation =>
                    {
                        instrumentation.RecordException = true;

                        // The health probe and the scrape endpoint produce one span each, a
                        // few times a minute, forever. They would outnumber everything worth
                        // looking at.
                        instrumentation.Filter = context => !IsNoise(context.Request.Path);
                    })
                    .AddHttpClientInstrumentation()
                    .AddNpgsql();

                if (otlpEndpoint is not null)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(resource)
                    .AddMeter(OrderTrackingDiagnostics.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    // The default boundaries start at five seconds, which would put every
                    // healthy outbox measurement in the first bucket and leave no way to tell
                    // a hundred-millisecond lag from a four-second one. These start below the
                    // polling interval and reach far enough to show a backlog forming.
                    .AddView(
                        "ordertracking.outbox.lag",
                        new ExplicitBucketHistogramConfiguration
                        {
                            Boundaries = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 30, 60, 300]
                        })
                    .AddPrometheusExporter();
            });

        return builder;
    }

    /// <summary>
    /// Adds request logging and the Prometheus scrape endpoint.
    /// </summary>
    /// <param name="app">The application being built.</param>
    /// <returns>The same application, for chaining.</returns>
    /// <remarks>
    /// Serilog's request logging replaces the framework's three lines per request with one
    /// event carrying the method, the path, the status and the elapsed time. Health probes and
    /// scrapes are logged at <see cref="LogEventLevel.Verbose"/>, which nothing writes, so a
    /// container that is merely alive produces no log at all.
    /// </remarks>
    public static WebApplication UseOrderTrackingObservability(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseSerilogRequestLogging(options =>
            options.GetLevel = (context, _, exception) =>
                exception is not null || context.Response.StatusCode >= 500 ? LogEventLevel.Error
                : context.Response.StatusCode >= 400 ? LogEventLevel.Warning
                : IsNoise(context.Request.Path) ? LogEventLevel.Verbose
                : LogEventLevel.Information);

        app.MapPrometheusScrapingEndpoint();

        return app;
    }

    private static bool IsNoise(PathString path) =>
        path.StartsWithSegments("/health") || path.StartsWithSegments("/metrics");
}

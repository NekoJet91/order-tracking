using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi.Models;
using OrderTracking.Api.Contracts.Orders;
using OrderTracking.Api.Features.Orders;
using OrderTracking.Api.Http;
using OrderTracking.Api.Observability;
using OrderTracking.Api.Realtime;
using OrderTracking.Infrastructure;
using OrderTracking.Infrastructure.Messaging;
using OrderTracking.Infrastructure.Outbox;

var builder = WebApplication.CreateBuilder(args);

builder.AddOrderTrackingLogging();
builder.AddOrderTrackingTelemetry();

// Injected rather than calling DateTimeOffset.UtcNow at the point of use, so that tests
// can advance the clock deterministically with FakeTimeProvider.
builder.Services.AddSingleton(TimeProvider.System);

// Statuses travel as "Shipped", not 2. Readable in a browser, stable if the enum is ever
// reordered, and it gives OpenAPI a real enum schema for the TypeScript client to mirror.
//
// Applied twice on purpose. Minimal APIs serialize through Http.Json.JsonOptions, while
// Swashbuckle builds schemas from Mvc.JsonOptions — two unrelated options objects. Set only
// the first and the API returns "Shipped" while the OpenAPI document promises 2, so every
// generated client is wrong in the same place.
// allowIntegerValues: false because the published schema says these are strings. Left at
// the default, the API would also accept {"status": 2} — undocumented behavior
static void ConfigureJson(JsonSerializerOptions options) =>
    options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));

builder.Services.ConfigureHttpJsonOptions(options => ConfigureJson(options.SerializerOptions));
builder.Services.Configure<JsonOptions>(options => ConfigureJson(options.JsonSerializerOptions));

builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance =
            $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";

        // ASP.NET Core already starts an Activity per request, so this is a real W3C
        // traceparent today and becomes the key that stitches the HTTP request to the
        // outbox row, the broker message and the socket push once tracing is exported.
        context.ProblemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
    });

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddValidatorsFromAssemblyContaining<CreateOrderRequestValidator>(
    // Validators here are stateless rule sets, so one instance per application is enough.
    ServiceLifetime.Singleton);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Order Tracking API",
        Version = "v1",
        Description = "Create orders, follow their status, and receive status changes over a WebSocket."
    });

    // Every project in the solution emits documentation, so the schemas rendered in Swagger
    // carry the same comments the code does and cannot drift from them.
    foreach (var documentation in Directory.EnumerateFiles(AppContext.BaseDirectory, "OrderTracking.*.xml"))
    {
        options.IncludeXmlComments(documentation);
    }

    // Nullable reference types are on, so the compiler already knows which fields can be
    // absent. This carries that into the schema instead of marking everything optional.
    options.SupportNonNullableReferenceTypes();
});

var connectionString = builder.Configuration.GetConnectionString("OrderTracking")
    ?? throw new InvalidOperationException(
        "Connection string 'OrderTracking' is not configured. Set it with user-secrets for "
        + "local development, or via the ConnectionStrings__OrderTracking environment "
        + "variable when running in a container.");

builder.Services.AddOrderTrackingInfrastructure(connectionString);

builder.Services.AddOrderTrackingMessaging(
    options => builder.Configuration.GetSection(RabbitMqOptions.SectionName).Bind(options),
    options => builder.Configuration.GetSection(OutboxOptions.SectionName).Bind(options),
    options => builder.Configuration.GetSection(RetentionOptions.SectionName).Bind(options));

builder.Services.Configure<OrderSocketOptions>(
    builder.Configuration.GetSection(OrderSocketOptions.SectionName));

// Holds the live sockets, so it must outlive any request: a singleton is not a convenience
// here, it is the only lifetime that works.
builder.Services.AddSingleton<OrderSocketConnectionManager>();

// Replace rather than Add, so this does not depend on running before or after the
// infrastructure registration that supplies the logging fallback.
builder.Services.Replace(
    ServiceDescriptor.Scoped<IOrderEventHandler, WebSocketOrderEventHandler>());

builder.Services.AddHostedService<HeartbeatService>();

var simulator = builder.Configuration
    .GetSection(StatusSimulatorOptions.SectionName)
    .Get<StatusSimulatorOptions>() ?? new StatusSimulatorOptions();

if (simulator.Enabled)
{
    // Registered only when switched on, so the class does not exist in any other
    // configuration rather than existing and being asked to keep quiet.
    builder.Services.Configure<StatusSimulatorOptions>(
        builder.Configuration.GetSection(StatusSimulatorOptions.SectionName));
    builder.Services.AddHostedService<StatusSimulator>();
}

var app = builder.Build();

// First in the pipeline, and registered in every environment rather than only outside
// Development: the developer exception page would make the tests assert on a different
// response shape than production returns.
app.UseExceptionHandler();

app.UseOrderTrackingObservability();

// KeepAliveInterval makes the server send protocol-level ping frames. Browsers answer them
// in the network stack, which is what keeps an idle connection from being reaped by a proxy
// — but JavaScript never sees them, which is why there is an application-level heartbeat
// as well.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .WithTags("Diagnostics")
   .WithSummary("Liveness probe.");

app.MapOrderEndpoints();
app.MapOrderSocketEndpoints();

app.Run();

/// <summary>
/// Entry point marker. Declared explicitly so integration tests can boot the real
/// application through <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program;

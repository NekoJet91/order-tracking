using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using OrderTracking.Domain.Orders;

namespace OrderTracking.Api.Http;

/// <summary>
/// Last line of defense: turns any unhandled exception into an RFC 9457 problem response.
/// </summary>
/// <param name="problemDetailsService">Writes the response in the configured problem format.</param>
/// <param name="environment">Used to decide whether internal detail may be disclosed.</param>
/// <param name="logger">Receives the exception.</param>
/// <remarks>
/// <para>
/// Failures the endpoints anticipate — an unknown order number, a transition the state
/// machine forbids, a lost concurrency race — are returned as results at the point they
/// occur. This class exists for everything else, so that no code path can return a bare
/// framework error page.
/// </para>
/// <para>
/// It still maps the domain and concurrency exceptions, because those can also escape from
/// somewhere that did not anticipate them, and a <c>409</c> is a better answer than a
/// <c>500</c> in that case too.
/// </para>
/// </remarks>
public sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var (statusCode, title) = exception switch
        {
            BadHttpRequestException =>
                (StatusCodes.Status400BadRequest, "Malformed request"),
            InvalidOrderStatusTransitionException =>
                (StatusCodes.Status409Conflict, "Status change not permitted"),
            DbUpdateConcurrencyException =>
                (StatusCodes.Status409Conflict, "Order was modified concurrently"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error")
        };

        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandled(logger, httpContext.Request.Method, httpContext.Request.Path, exception);
        }
        else
        {
            LogRejected(logger, statusCode, httpContext.Request.Method, httpContext.Request.Path, exception);
        }

        httpContext.Response.StatusCode = statusCode;

        // An unexpected exception's message can name internal types, file paths or SQL, so
        // it is only disclosed outside production.
        var detail = statusCode < StatusCodes.Status500InternalServerError || environment.IsDevelopment()
            ? exception.Message
            : "The request could not be completed. Quote the traceId when reporting this.";

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = statusCode, Title = title, Detail = detail }
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}.")]
    private static partial void LogUnhandled(ILogger logger, string method, PathString path, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Request rejected with {StatusCode} for {Method} {Path}.")]
    private static partial void LogRejected(
        ILogger logger, int statusCode, string method, PathString path, Exception exception);
}

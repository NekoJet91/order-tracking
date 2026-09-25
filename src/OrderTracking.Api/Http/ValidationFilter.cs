using FluentValidation;

namespace OrderTracking.Api.Http;

/// <summary>
/// Endpoint filter that runs the registered FluentValidation validator for
/// <typeparamref name="TRequest"/> and short-circuits with <c>400</c> if it fails.
/// </summary>
/// <typeparam name="TRequest">The request body type to validate.</typeparam>
/// <remarks>
/// The validator is resolved from <c>HttpContext.RequestServices</c> rather than injected
/// through the constructor. <c>AddEndpointFilter&lt;T&gt;</c> builds the filter once from the
/// root provider, so a constructor-injected scoped validator would be a captive dependency
/// and would throw at startup. Resolving per request keeps the filter safe at any lifetime.
/// </remarks>
public sealed class ValidationFilter<TRequest> : IEndpointFilter
    where TRequest : class
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();

        if (request is null)
        {
            return await next(context);
        }

        var validator = context.HttpContext.RequestServices.GetRequiredService<IValidator<TRequest>>();
        var result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);

        return result.IsValid
            ? await next(context)
            : TypedResults.ValidationProblem(result.ToDictionary());
    }
}

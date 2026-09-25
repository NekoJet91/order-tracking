using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace OrderTracking.IntegrationTests;

/// <summary>
/// Container surgery used only by tests.
/// </summary>
internal static class ServiceCollectionTestExtensions
{
    /// <summary>
    /// Unregisters one hosted service, leaving every other one alone.
    /// </summary>
    /// <typeparam name="THostedService">The implementation to remove.</typeparam>
    /// <param name="services">The container being built.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// Named for what it must not do: <c>RemoveAll&lt;IHostedService&gt;()</c> would also
    /// remove the web host's own <c>GenericWebHostService</c> and leave a test server that
    /// never starts. Matching on the implementation type keeps the removal surgical.
    /// </remarks>
    public static IServiceCollection RemoveHostedService<THostedService>(this IServiceCollection services)
        where THostedService : IHostedService
    {
        var descriptors = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                                 && descriptor.ImplementationType == typeof(THostedService))
            .ToArray();

        foreach (var descriptor in descriptors)
        {
            services.Remove(descriptor);
        }

        return services;
    }
}

namespace OrderTracking.Domain.Orders;

/// <summary>
/// Allocates the next unique <see cref="Order.OrderNumber"/>.
/// </summary>
/// <remarks>
/// Declared here and implemented in the infrastructure layer.
/// The implementation must be safe under concurrency
/// </remarks>
public interface IOrderNumberGenerator
{
    /// <summary>Allocates the next order number.</summary>
    /// <param name="cancellationToken">Cancels the allocation.</param>
    /// <returns>A number that has not been issued before.</returns>
    Task<string> NextAsync(CancellationToken cancellationToken = default);
}

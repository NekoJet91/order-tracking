using Microsoft.EntityFrameworkCore;
using OrderTracking.Domain.Orders;

namespace OrderTracking.Infrastructure.Persistence;

/// <summary>
/// Allocates order numbers from a PostgreSQL sequence.
/// </summary>
/// <remarks>
/// A sequence is used rather than <c>SELECT max(...) + 1</c> because the latter is a
/// read-modify-write race. <c>nextval</c> is atomic and never returns the same value twice,
/// even to concurrent sessions and even if the surrounding transaction later rolls back —
/// which is why numbering can have gaps but never duplicates. Gaps are acceptable; a
/// duplicate would violate the unique index and fail the request.
/// </remarks>
/// <param name="dbContext">The context whose connection issues the allocation.</param>
internal sealed class SequenceOrderNumberGenerator(OrderTrackingDbContext dbContext)
    : IOrderNumberGenerator
{
    private const string _nextValueSql = $"""SELECT nextval('{OrderTrackingDbContext.OrderNumberSequenceName}') AS "Value" """;

    /// <inheritdoc />
    public async Task<string> NextAsync(CancellationToken cancellationToken = default)
    {
        var next = await dbContext.Database
            .SqlQueryRaw<long>(_nextValueSql)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        return $"ORD-{next:D8}";
    }
}

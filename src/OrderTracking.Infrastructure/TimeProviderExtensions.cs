namespace OrderTracking.Infrastructure;

/// <summary>
/// Clock helpers aligned with what this system's store can actually represent.
/// </summary>
public static class TimeProviderExtensions
{
    /// <summary>
    /// The current UTC time, truncated to the microsecond.
    /// </summary>
    /// <param name="timeProvider">The clock to read.</param>
    /// <returns>A timestamp that survives a round trip through the database unchanged.</returns>
    /// <remarks>
    /// A .NET tick is 100 ns and PostgreSQL <c>timestamptz</c> stores microseconds, so Npgsql
    /// drops the last digit on the way in, silently. Untruncated, the response to a write
    /// reports a timestamp the database never held and the next read of the same row returns
    /// a different one.
    /// </remarks>
    public static DateTimeOffset GetStorableUtcNow(this TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        var ticks = timeProvider.GetUtcNow().UtcTicks;

        return new DateTimeOffset(ticks - (ticks % TimeSpan.TicksPerMicrosecond), TimeSpan.Zero);
    }
}

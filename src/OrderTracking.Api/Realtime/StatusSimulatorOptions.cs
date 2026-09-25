namespace OrderTracking.Api.Realtime;

/// <summary>
/// Settings for the demo traffic generator.
/// </summary>
public sealed class StatusSimulatorOptions
{
    /// <summary>Configuration section these settings are bound from.</summary>
    public const string SectionName = "StatusSimulator";

    /// <summary>Whether to generate demo traffic at all.</summary>
    /// <remarks>
    /// Off unless switched on. A background job that writes orders by itself is useful for
    /// showing the live screen doing something and unacceptable anywhere real, so the
    /// default has to be the safe one.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>How long to wait between actions.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>How many orders may be in flight at once.</summary>
    /// <remarks>
    /// <para>
    /// Less a brake than a switch. Below the ceiling each tick either creates an order or
    /// moves one along; at the ceiling it only moves them, which drains the pool, because
    /// delivered and canceled orders stop counting as open. The result settles just under
    /// the ceiling rather than stopping dead at it.
    /// </para>
    /// <para>
    /// Some ceiling is required regardless: without one the demo quietly fills the database
    /// over a lunch break.
    /// </para>
    /// </remarks>
    public int MaxOpenOrders { get; set; } = 12;
}

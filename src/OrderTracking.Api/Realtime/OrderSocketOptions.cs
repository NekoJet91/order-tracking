namespace OrderTracking.Api.Realtime;

/// <summary>
/// Tuning for the order notification socket.
/// </summary>
public sealed class OrderSocketOptions
{
    /// <summary>Configuration section these settings are bound from.</summary>
    public const string SectionName = "OrderSocket";

    /// <summary>How many orders the opening snapshot carries.</summary>
    /// <remarks>
    /// Matches the first page of the list endpoint. A snapshot is what fills a screen, not a
    /// copy of the table, and a client that wants older orders pages back through HTTP.
    /// </remarks>
    public int SnapshotSize { get; set; } = 20;

    /// <summary>How many frames may be waiting for one client before it is disconnected.</summary>
    /// <remarks>
    /// Deep enough to absorb a burst, shallow enough that a client which has genuinely
    /// stopped reading is noticed in seconds rather than after it has cost real memory.
    /// </remarks>
    public int SendQueueCapacity { get; set; } = 64;

    /// <summary>How often an application-level heartbeat is sent.</summary>
    /// <remarks>
    /// Shorter than the idle timeout of a typical reverse proxy — sixty seconds is a common
    /// default — so that a quiet connection is not mistaken for an abandoned one and closed
    /// by something in the middle.
    /// </remarks>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(20);
}

using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace OrderTracking.Api.Features.Orders;

/// <summary>
/// Position of the last row of a page, used to fetch the next one.
/// </summary>
/// <param name="CreatedAt">Creation timestamp of the last row returned.</param>
/// <param name="Id">Surrogate key of the last row returned, breaking ties on the timestamp.</param>
/// <remarks>
/// <para>
/// The wire form is base64url of <c>{utcTicks}:{id}</c>. Encoding it serves one purpose:
/// making it obvious that the token is opaque. That is what lets it carry the surrogate
/// key, which the API otherwise never exposes, and what lets the sort key change later
/// without breaking clients that stored a cursor.
/// </para>
/// <para>
/// Opaque is not secret. The token is base64, not encrypted, and it is not signed, so
/// nothing that affects authorization may ever be stored in it.
/// </para>
/// <para>
/// Ticks are 100 ns and PostgreSQL <c>timestamptz</c> stores microseconds, so a value read
/// from the database round-trips through this exactly.
/// </para>
/// </remarks>
public readonly record struct OrderCursor(DateTimeOffset CreatedAt, long Id)
{
    /// <summary>Renders the cursor as the opaque string handed to the client.</summary>
    /// <returns>A base64url token.</returns>
    public string Encode()
    {
        var raw = string.Create(
            CultureInfo.InvariantCulture, $"{CreatedAt.UtcTicks}:{Id}");

        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>Parses a token produced by <see cref="Encode"/>.</summary>
    /// <param name="value">The token received from the client.</param>
    /// <param name="cursor">The decoded cursor, if parsing succeeded.</param>
    /// <returns><c>true</c> if <paramref name="value"/> was a well-formed cursor.</returns>
    /// <remarks>
    /// A cursor is client-supplied input that arrived through several hops, so every
    /// failure mode — bad base64, missing separator, unparseable numbers, out-of-range
    /// ticks — is a <c>false</c> rather than an exception.
    /// </remarks>
    public static bool TryDecode(string? value, out OrderCursor cursor)
    {
        cursor = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string raw;
        try
        {
            raw = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(value));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = raw.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        if (!long.TryParse(raw.AsSpan(0, separator), CultureInfo.InvariantCulture, out var ticks)
            || !long.TryParse(raw.AsSpan(separator + 1), CultureInfo.InvariantCulture, out var id)
            || ticks < DateTimeOffset.MinValue.UtcTicks
            || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        cursor = new OrderCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        return true;
    }
}

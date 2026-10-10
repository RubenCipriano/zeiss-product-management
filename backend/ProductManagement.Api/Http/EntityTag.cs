using System.Globalization;
using Microsoft.Net.Http.Headers;

namespace ProductManagement.Api.Http;

/// <summary>
/// Reads and writes entity tags: the product's version in quotes (e.g. <c>"123"</c>), sent as the <c>ETag</c> header.
/// The client sends it back in <c>If-Match</c> ("only change it if it is still this version") or
/// <c>If-None-Match</c> ("only send it if it changed"). RFC 9110 §8.8.3.
/// </summary>
internal static class EntityTag
{
    public static string Format(uint version) => string.Create(CultureInfo.InvariantCulture, $"\"{version}\"");

    /// <summary>
    /// Parses an <c>If-Match</c> header value.
    /// </summary>
    /// <param name="headerValue">The raw header value; <c>null</c> when the header was not sent.</param>
    /// <param name="version">
    /// The expected version, or <c>null</c> when no precondition applies (absent or <c>*</c>).
    /// </param>
    /// <returns><c>false</c> when the header is present but is not a single strong entity tag.</returns>
    public static bool TryParseIfMatch(string? headerValue, out uint? version)
    {
        version = null;
        var value = headerValue?.Trim();

        if (string.IsNullOrEmpty(value) || value == "*")
        {
            return true;
        }

        // Weak tags (W/"...") are not allowed for If-Match; lists of tags are not supported.
        if (value.Length < 3 || value[0] != '"' || value[^1] != '"')
        {
            return false;
        }

        var quoted = value.AsSpan(1, value.Length - 2);
        if (!uint.TryParse(quoted, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        version = parsed;
        return true;
    }

    /// <summary>
    /// Whether an <c>If-None-Match</c> header value names the current version, i.e. the client's copy is current and a
    /// <c>304 Not Modified</c> can replace the body (RFC 9110 §13.1.2): <c>*</c>, or any tag of the list under weak
    /// comparison. An absent or malformed header matches nothing, so the full response is sent.
    /// </summary>
    public static bool MatchesIfNoneMatch(string? headerValue, uint version)
    {
        if (string.IsNullOrWhiteSpace(headerValue)
            || !EntityTagHeaderValue.TryParseList([headerValue], out var tags))
        {
            return false;
        }

        var current = new EntityTagHeaderValue(Format(version));
        return tags.Any(tag =>
            tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(current, useStrongComparison: false));
    }
}

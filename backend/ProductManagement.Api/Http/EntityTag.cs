using System.Globalization;

namespace ProductManagement.Api.Http;

/// <summary>
/// Converts between a resource version and an HTTP entity tag (<c>ETag</c> / <c>If-Match</c>, RFC 9110 §8.8.3).
/// </summary>
internal static class EntityTag
{
    public static string Format(uint version) => string.Create(CultureInfo.InvariantCulture, $"\"{version}\"");

    /// <summary>
    /// Parses an <c>If-Match</c> header value.
    /// </summary>
    /// <param name="headerValue">The raw header value; <c>null</c> when the header was not sent.</param>
    /// <param name="version">The expected version, or <c>null</c> when no precondition applies (absent or <c>*</c>).</param>
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

        if (!uint.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        version = parsed;
        return true;
    }
}

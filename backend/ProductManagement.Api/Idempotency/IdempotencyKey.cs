using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace ProductManagement.Api.Idempotency;

/// <summary>
/// The <c>Idempotency-Key</c> header: which values are accepted, and how a key is scoped to its caller.
/// </summary>
internal static class IdempotencyKey
{
    public const string HeaderName = "Idempotency-Key";

    public const int MaxLength = 128;

    public static readonly string Rule =
        $"'{HeaderName}' must be 1-{MaxLength} characters of letters, digits, '-', '_', '.' or ':'.";

    private static readonly SearchValues<char> AllowedCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.:");

    /// <summary>
    /// Validates <paramref name="raw"/> and scopes it to <paramref name="subject"/> (<c>{sub}:{key}</c>), so consumers
    /// can never see or block each other's keys.
    /// </summary>
    public static bool TryParse(string raw, string subject, [NotNullWhen(true)] out string? scopedKey)
    {
        if (raw.Length is 0 or > MaxLength || raw.AsSpan().ContainsAnyExcept(AllowedCharacters))
        {
            scopedKey = null;
            return false;
        }

        scopedKey = $"{subject}:{raw}";
        return true;
    }
}

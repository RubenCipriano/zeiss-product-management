namespace ProductManagement.Domain.Common;

internal static class Guard
{
    public static string AgainstInvalidText(string? value, int maxLength, string fieldName)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException($"{fieldName} is required.");
        }

        if (trimmed.Length > maxLength)
        {
            throw new DomainException($"{fieldName} must not exceed {maxLength} characters.");
        }

        return trimmed;
    }

    public static string? AgainstTooLongOptionalText(string? value, int maxLength, string fieldName)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            throw new DomainException($"{fieldName} must not exceed {maxLength} characters.");
        }

        return trimmed;
    }

    public static void AgainstOutOfRange<T>(T value, T min, T max, string fieldName)
        where T : IComparable<T>
    {
        if (value.CompareTo(min) < 0 || value.CompareTo(max) > 0)
        {
            throw new DomainException($"{fieldName} must be between {min} and {max}.");
        }
    }
}

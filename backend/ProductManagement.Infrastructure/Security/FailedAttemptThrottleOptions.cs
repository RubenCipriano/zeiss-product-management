using System.ComponentModel.DataAnnotations;

namespace ProductManagement.Infrastructure.Security;

/// <summary>
/// Sign-in throttle settings (the <c>SignInThrottle</c> section of the configuration): how many failed sign-ins a
/// caller may make within a time window before it is blocked for the rest of that window.
/// </summary>
internal class FailedAttemptThrottleOptions
{
    public const string SectionName = "SignInThrottle";

    /// <summary>Failed sign-ins allowed per key within <see cref="Window"/>; the next ones are refused.</summary>
    [Range(1, int.MaxValue)]
    public int MaxFailures { get; init; } = 10;

    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);
}

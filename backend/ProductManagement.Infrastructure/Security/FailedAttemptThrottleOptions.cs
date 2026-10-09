using System.ComponentModel.DataAnnotations;

namespace ProductManagement.Infrastructure.Security;

internal sealed class FailedAttemptThrottleOptions
{
    public const string SectionName = "SignInThrottle";

    /// <summary>Failed sign-ins allowed per key within <see cref="Window"/>; the next ones are refused.</summary>
    [Range(1, int.MaxValue)]
    public int MaxFailures { get; init; } = 10;

    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);
}

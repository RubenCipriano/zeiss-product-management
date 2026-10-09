namespace ProductManagement.Infrastructure.Security;

public sealed class FailedAttemptThrottleOptions
{
    public const string SectionName = "SignInThrottle";

    /// <summary>Failed sign-ins allowed per key within <see cref="Window"/>; the next ones are refused.</summary>
    public int MaxFailures { get; init; } = 10;

    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);
}

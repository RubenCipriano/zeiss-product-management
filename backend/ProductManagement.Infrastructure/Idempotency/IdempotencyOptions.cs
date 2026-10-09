namespace ProductManagement.Infrastructure.Idempotency;

public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    /// <summary>How long a processed key (and its response) is remembered.</summary>
    public TimeSpan KeyLifetime { get; init; } = TimeSpan.FromHours(24);

    /// <summary>How long a key stays claimed while its request runs; protects against a replica dying mid-request.</summary>
    public TimeSpan InProgressTimeout { get; init; } = TimeSpan.FromMinutes(1);
}

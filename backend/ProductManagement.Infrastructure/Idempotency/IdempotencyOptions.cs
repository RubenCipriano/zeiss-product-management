using System.ComponentModel.DataAnnotations;

namespace ProductManagement.Infrastructure.Idempotency;

/// <summary>Idempotency settings (the <c>Idempotency</c> section of the configuration).</summary>
internal class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    /// <summary>How long a processed key (and its response) is remembered.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "30.00:00:00")]
    public TimeSpan KeyLifetime { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a key stays claimed while its request runs; protects against a replica dying mid-request.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan InProgressTimeout { get; init; } = TimeSpan.FromMinutes(1);
}

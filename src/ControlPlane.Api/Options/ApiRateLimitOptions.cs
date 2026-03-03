namespace ControlPlane.Api.Options;

public sealed class ApiRateLimitOptions
{
    public const string SectionName = "ApiRateLimit";

    public FixedWindowBucketOptions PublicAuth { get; set; } = new();
    public FixedWindowBucketOptions PublicClient { get; set; } = new()
    {
        PermitLimit = 240
    };
    public FixedWindowBucketOptions ServerApi { get; set; } = new()
    {
        PermitLimit = 3000
    };
    public FixedWindowBucketOptions InternalApi { get; set; } = new()
    {
        PermitLimit = 600
    };
}

public sealed class FixedWindowBucketOptions
{
    public int PermitLimit { get; set; } = 60;
    public int WindowSeconds { get; set; } = 60;
    public int QueueLimit { get; set; }
}

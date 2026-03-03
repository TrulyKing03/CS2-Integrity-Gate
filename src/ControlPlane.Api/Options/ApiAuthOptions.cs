namespace ControlPlane.Api.Options;

public sealed class ApiAuthOptions
{
    public const string SectionName = "ApiAuth";

    public string ServerApiKey { get; set; } = "dev-server-api-key";
    public string InternalApiKey { get; set; } = "dev-internal-api-key";
    public bool RequireQueueAccessToken { get; set; }
    public int AccessTokenTtlMinutes { get; set; } = 180;
}

namespace ControlPlane.Api.Options;

public sealed class EvidenceOptions
{
    public const string SectionName = "Evidence";

    public string StorageDirectory { get; set; } = "evidence";
    public int RecentTelemetryLimit { get; set; } = 300;
    public int RecentHeartbeatsLimit { get; set; } = 30;
}

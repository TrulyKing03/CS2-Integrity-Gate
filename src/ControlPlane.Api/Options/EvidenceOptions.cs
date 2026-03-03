namespace ControlPlane.Api.Options;

public sealed class EvidenceOptions
{
    public const string SectionName = "Evidence";

    public string StorageDirectory { get; set; } = "evidence";
    public int RecentTelemetryLimit { get; set; } = 300;
    public int RecentHeartbeatsLimit { get; set; } = 30;
    public bool AutoCreateReviewCases { get; set; } = true;
    public string AutoReviewPriority { get; set; } = "high";
    public string AutoReviewRequestedBy { get; set; } = "auto_detection";
    public List<string> AutoReviewTriggerAllowList { get; set; } =
    [
        "rules_impossible_state",
        "rules_fire_cadence"
    ];
}

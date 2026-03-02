namespace ControlPlane.Api.Options;

public sealed class AcPolicyOptions
{
    public const string SectionName = "AcPolicy";

    public string JoinTokenSecret { get; set; } = "replace-me";
    public int JoinTokenTtlSec { get; set; } = 90;
    public int HeartbeatIntervalSec { get; set; } = 10;
    public int GraceWindowSec { get; set; } = 60;
    public string DefaultQueueTier { get; set; } = "high_trust";
    public string PolicyVersion { get; set; } = "pol_local";
    public PlatformTierOptions RequiredTierA { get; set; } = new();
    public PlatformTierOptions RequiredTierB { get; set; } = new();
}

public sealed class PlatformTierOptions
{
    public bool SecureBoot { get; set; } = true;
    public bool Tpm20 { get; set; } = true;
    public bool Iommu { get; set; }
    public bool Vbs { get; set; }
}

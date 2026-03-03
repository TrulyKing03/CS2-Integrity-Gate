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
    public bool RequireAntiTamperOnMatchStart { get; set; } = true;
    public List<string> RequiredPolicyHashes { get; set; } = new();
    public PlatformTierOptions RequiredTierA { get; set; } = new();
    public PlatformTierOptions RequiredTierB { get; set; } = new();
    public DetectionThresholdOptions Detection { get; set; } = new();
}

public sealed class PlatformTierOptions
{
    public bool SecureBoot { get; set; } = true;
    public bool Tpm20 { get; set; } = true;
    public bool Iommu { get; set; }
    public bool Vbs { get; set; }
}

public sealed class DetectionThresholdOptions
{
    public int MinAimSamples { get; set; } = 30;
    public int MinTriggerSamples { get; set; } = 20;
    public int MinInfoSamples { get; set; } = 25;
    public int MinMovementSamples { get; set; } = 80;
    public double MovementScoreScale { get; set; } = 220.0;
    public double AimHitRatioBaseline { get; set; } = 0.45;
    public double AimScoreScale { get; set; } = 180.0;
    public double TriggerScoreScale { get; set; } = 170.0;
    public double InfoScoreScale { get; set; } = 160.0;
    public double RulesViolationScoreScale { get; set; } = 20.0;
    public int RulesTickActionMinViolations { get; set; } = 3;
    public int RulesShotActionMinViolations { get; set; } = 4;
    public int BehaviorActionMinSamples { get; set; } = 40;
    public double MovementActionScoreThreshold { get; set; } = 97.0;
    public double AimActionScoreThreshold { get; set; } = 98.0;
    public double TriggerActionScoreThreshold { get; set; } = 98.0;
    public double InfoActionScoreThreshold { get; set; } = 98.0;
    public string BehaviorActionType { get; set; } = "queue_restrict";
    public int BehaviorActionDurationSec { get; set; } = 3600;
    public int ActionCooldownSec { get; set; } = 30;
}

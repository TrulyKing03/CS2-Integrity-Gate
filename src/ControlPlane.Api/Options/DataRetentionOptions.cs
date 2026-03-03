namespace ControlPlane.Api.Options;

public sealed class DataRetentionOptions
{
    public const string SectionName = "DataRetention";

    public bool Enabled { get; set; } = true;
    public bool RunOnStartup { get; set; } = true;
    public int SweepIntervalMinutes { get; set; } = 15;
    public int JoinTokenRetentionMinutes { get; set; } = 180;
    public int HeartbeatRetentionHours { get; set; } = 72;
    public int TelemetryRetentionHours { get; set; } = 72;
    public int SecurityEventRetentionHours { get; set; } = 168;
}

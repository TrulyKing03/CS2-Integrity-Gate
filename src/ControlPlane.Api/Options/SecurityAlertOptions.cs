namespace ControlPlane.Api.Options;

public sealed class SecurityAlertOptions
{
    public const string SectionName = "SecurityAlerts";

    public bool Enabled { get; set; } = true;
    public int SweepIntervalSeconds { get; set; } = 30;
    public int WindowMinutes { get; set; } = 10;
    public int MediumThreshold { get; set; } = 20;
    public int HighThreshold { get; set; } = 1;
    public int CooldownMinutes { get; set; } = 5;
}

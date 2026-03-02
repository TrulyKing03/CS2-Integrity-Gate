namespace ControlPlane.Api.Options;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string SqlitePath { get; set; } = "data/controlplane.db";
}

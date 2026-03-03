namespace AcClient.Service.Options;

public sealed class AcClientOptions
{
    public const string SectionName = "AcClient";

    public string BackendBaseUrl { get; set; } = "http://localhost:5042";
    public string AccountId { get; set; } = "acc_local_demo";
    public string SteamId { get; set; } = "76561190000000001";
    public string LauncherVersion { get; set; } = "0.1.0";
    public string AcVersion { get; set; } = "0.1.0";
    public string SessionFilePath { get; set; } = "runtime/session.json";
    public string SessionSignaturePath { get; set; } = "runtime/session.sig";
    public string JoinTokenOutputPath { get; set; } = "runtime/join-token.json";
    public string DeviceKeyPath { get; set; } = "runtime/device-key.pem";
    public string PolicyHash { get; set; } = "sha256:local_policy_hash";
    public string RuntimeSigningKey { get; set; } =
        Environment.GetEnvironmentVariable("CS2IG_RUNTIME_SIGNING_KEY") ?? string.Empty;
}

namespace Cs2.Plugin.CounterStrikeSharp;

public sealed class PluginRuntimeOptions
{
    public string BackendBaseUrl { get; set; } = "http://localhost:5042";
    public string ServerApiKey { get; set; } = "dev-server-api-key";
    public string ExecutorId { get; set; } = "css-plugin";
    public string TelemetrySource { get; set; } = "cs2_plugin_counterstrikesharp";
    public int MaxBatchSize { get; set; } = 64;
    public int TelemetryFlushSec { get; set; } = 3;
    public int HealthPollSec { get; set; } = 5;
    public int ActionPollSec { get; set; } = 3;
    public int PendingActionFetchLimit { get; set; } = 200;
    public int ActionApplyDedupeSec { get; set; } = 120;
    public int ActionAckRetrySec { get; set; } = 5;
}

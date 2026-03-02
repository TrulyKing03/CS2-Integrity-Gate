using Cs2.Plugin.CounterStrikeSharp;
using Shared.Contracts;

var options = AgentOptions.Parse(args);
var hostBridge = new DemoHostBridge();

var runtimeOptions = new PluginRuntimeOptions
{
    BackendBaseUrl = options.BackendBaseUrl,
    ServerApiKey = options.ServerApiKey,
    ExecutorId = options.ExecutorId,
    TelemetrySource = "server_bridge_agent_runtime",
    MaxBatchSize = options.MaxBatchSize,
    TelemetryFlushSec = options.FlushSec
};

using var runtime = new PluginRuntime(runtimeOptions, hostBridge);

await runtime.HandleConnectionAttemptAsync(
    new PlayerConnectionAttempt(
        options.MatchSessionId,
        options.ServerId,
        options.AccountId,
        options.SteamId,
        options.JoinToken),
    CancellationToken.None);

if (!hostBridge.ConnectionAccepted)
{
    Console.WriteLine($"Join denied: {hostBridge.DenyReason ?? "unknown"}");
    return;
}

Console.WriteLine($"Join accepted for account={options.AccountId}, match={options.MatchSessionId}");

var tick = options.StartTick;
var loopUntil = DateTimeOffset.UtcNow.AddSeconds(options.RuntimeSec);
while (DateTimeOffset.UtcNow < loopUntil)
{
    tick += options.SimulateCheat ? 1 : 6;

    await runtime.CaptureTickAsync(
        BuildTick(options, tick),
        CancellationToken.None);

    await runtime.CaptureVisibilityAsync(
        new VisibilitySample(
            options.MatchSessionId,
            tick - 1,
            DateTimeOffset.UtcNow,
            options.AccountId,
            options.TargetAccountId,
            LineOfSight: !options.SimulateCheat,
            AudibleProxy: false,
            DistanceMeters: 18.3f),
        CancellationToken.None);

    await runtime.CaptureShotAsync(
        new ShotSample(
            options.MatchSessionId,
            tick,
            DateTimeOffset.UtcNow,
            options.AccountId,
            options.SteamId,
            options.WeaponId,
            RecoilIndex: options.SimulateCheat ? 0 : 3,
            Yaw: 120.5f,
            Pitch: -1.2f,
            HitPlayer: true,
            HitAccountId: options.TargetAccountId,
            HitSteamId: options.TargetSteamId),
        CancellationToken.None);

    await runtime.PollHealthAndEnforceAsync(options.MatchSessionId, CancellationToken.None);
    await runtime.PollPendingActionsAndApplyAsync(options.MatchSessionId, options.AccountId, CancellationToken.None);

    await Task.Delay(TimeSpan.FromSeconds(options.TickIntervalSec));
}

await runtime.FlushTelemetryAsync(options.MatchSessionId, CancellationToken.None);

Console.WriteLine($"Server bridge simulation complete. AppliedActions={hostBridge.AppliedActions.Count}");

static TickSample BuildTick(AgentOptions options, long tick)
{
    var speed = options.SimulateCheat ? 480f : 260f;
    return new TickSample(
        options.MatchSessionId,
        tick,
        DateTimeOffset.UtcNow,
        options.AccountId,
        options.SteamId,
        Team: "CT",
        PosX: 12.2f,
        PosY: 8.1f,
        PosZ: 1.4f,
        VelX: speed,
        VelY: 0.2f,
        VelZ: -0.1f,
        Yaw: 122.4f,
        Pitch: -0.7f,
        Stance: "standing",
        WeaponId: options.WeaponId,
        AmmoClip: 30,
        IsReloading: false,
        PingMs: 38,
        LossPct: 0.1f,
        ChokePct: 0.0f);
}

internal sealed class DemoHostBridge : IPluginHostBridge
{
    public bool ConnectionAccepted { get; private set; }
    public string? DenyReason { get; private set; }
    public List<EnforcementAction> AppliedActions { get; } = new();

    public Task DenyConnectionAsync(PlayerConnectionAttempt attempt, string reason, CancellationToken cancellationToken)
    {
        ConnectionAccepted = false;
        DenyReason = reason;
        LogWarning($"DenyConnection account={attempt.AccountId} match={attempt.MatchSessionId} reason={reason}");
        return Task.CompletedTask;
    }

    public Task AcceptConnectionAsync(PlayerConnectionAttempt attempt, CancellationToken cancellationToken)
    {
        ConnectionAccepted = true;
        LogInfo($"AcceptConnection account={attempt.AccountId} match={attempt.MatchSessionId}");
        return Task.CompletedTask;
    }

    public Task ApplyEnforcementActionAsync(EnforcementAction action, CancellationToken cancellationToken)
    {
        AppliedActions.Add(action);
        Console.WriteLine($"Action: {action.ActionType} ({action.ReasonCode}) at {action.CreatedAtUtc:O}");
        return Task.CompletedTask;
    }

    public void LogInfo(string message) => Console.WriteLine($"[INFO] {message}");
    public void LogWarning(string message) => Console.WriteLine($"[WARN] {message}");

    public void LogError(string message, Exception? exception = null)
    {
        if (exception is null)
        {
            Console.WriteLine($"[ERROR] {message}");
            return;
        }

        Console.WriteLine($"[ERROR] {message} :: {exception.GetType().Name}: {exception.Message}");
    }
}

internal sealed class AgentOptions
{
    public string BackendBaseUrl { get; private set; } = "http://localhost:5042";
    public string MatchSessionId { get; private set; } = string.Empty;
    public string ServerId { get; private set; } = string.Empty;
    public string AccountId { get; private set; } = string.Empty;
    public string SteamId { get; private set; } = string.Empty;
    public string JoinToken { get; private set; } = string.Empty;
    public string ServerApiKey { get; private set; } = "dev-server-api-key";
    public string ExecutorId { get; private set; } = "simulator-agent";
    public string TargetAccountId { get; private set; } = "acc_enemy";
    public string TargetSteamId { get; private set; } = "76561190000000009";
    public string WeaponId { get; private set; } = "ak47";
    public int RuntimeSec { get; private set; } = 30;
    public int TickIntervalSec { get; private set; } = 2;
    public int MaxBatchSize { get; private set; } = 64;
    public int FlushSec { get; private set; } = 3;
    public long StartTick { get; private set; } = 1000;
    public bool SimulateCheat { get; private set; }

    public static AgentOptions Parse(string[] args)
    {
        var options = new AgentOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--backend":
                    options.BackendBaseUrl = ReadValue(args, ++i, "--backend");
                    break;
                case "--match":
                    options.MatchSessionId = ReadValue(args, ++i, "--match");
                    break;
                case "--server":
                    options.ServerId = ReadValue(args, ++i, "--server");
                    break;
                case "--account":
                    options.AccountId = ReadValue(args, ++i, "--account");
                    break;
                case "--steam":
                    options.SteamId = ReadValue(args, ++i, "--steam");
                    break;
                case "--token":
                    options.JoinToken = ReadValue(args, ++i, "--token");
                    break;
                case "--server-api-key":
                    options.ServerApiKey = ReadValue(args, ++i, "--server-api-key");
                    break;
                case "--executor-id":
                    options.ExecutorId = ReadValue(args, ++i, "--executor-id");
                    break;
                case "--simulate-cheat":
                    options.SimulateCheat = true;
                    break;
                case "--runtime-sec":
                    options.RuntimeSec = int.Parse(ReadValue(args, ++i, "--runtime-sec"));
                    break;
                case "--max-batch-size":
                    options.MaxBatchSize = int.Parse(ReadValue(args, ++i, "--max-batch-size"));
                    break;
                case "--flush-sec":
                    options.FlushSec = int.Parse(ReadValue(args, ++i, "--flush-sec"));
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(options.MatchSessionId) ||
            string.IsNullOrWhiteSpace(options.ServerId) ||
            string.IsNullOrWhiteSpace(options.AccountId) ||
            string.IsNullOrWhiteSpace(options.SteamId) ||
            string.IsNullOrWhiteSpace(options.JoinToken))
        {
            throw new ArgumentException("Required args: --match --server --account --steam --token");
        }

        return options;
    }

    private static string ReadValue(string[] args, int index, string key)
    {
        if (index >= args.Length)
        {
            throw new ArgumentException($"Missing value for {key}");
        }

        return args[index];
    }
}

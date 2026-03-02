using System.Net.Http.Json;
using System.Text.Json;
using Shared.Contracts;

var options = AgentOptions.Parse(args);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

using var http = new HttpClient
{
    BaseAddress = new Uri(options.BackendBaseUrl.TrimEnd('/') + "/")
};

var validateRequest = new ValidateJoinRequest(
    options.ServerId,
    options.SteamId,
    options.AccountId,
    options.MatchSessionId,
    options.JoinToken);

var validateResponseHttp = await http.PostAsJsonAsync("v1/attestation/validate-join", validateRequest, jsonOptions);
validateResponseHttp.EnsureSuccessStatusCode();
var validate = await validateResponseHttp.Content.ReadFromJsonAsync<ValidateJoinResponse>(jsonOptions)
    ?? throw new InvalidOperationException("validate-join response was empty");

if (!validate.Allow)
{
    Console.WriteLine($"Join denied: {validate.Reason}");
    return;
}

Console.WriteLine($"Join accepted for account={options.AccountId}, match={options.MatchSessionId}");

var tick = options.StartTick;
var loopUntil = DateTimeOffset.UtcNow.AddSeconds(options.RuntimeSec);
while (DateTimeOffset.UtcNow < loopUntil)
{
    tick += options.SimulateCheat ? 1 : 6;

    var tickEnvelope = new TelemetryEnvelope<TickPlayerState>(
        options.MatchSessionId,
        "server_bridge_agent",
        DateTimeOffset.UtcNow,
        new[]
        {
            BuildTick(options, tick)
        });
    await PostAsync(http, "v1/telemetry/ticks", tickEnvelope, jsonOptions);

    var losEnvelope = new TelemetryEnvelope<LosSample>(
        options.MatchSessionId,
        "server_bridge_agent",
        DateTimeOffset.UtcNow,
        new[]
        {
            new LosSample(
                options.MatchSessionId,
                tick - 1,
                DateTimeOffset.UtcNow,
                options.AccountId,
                options.TargetAccountId,
                LineOfSight: !options.SimulateCheat,
                AudibleProxy: false,
                DistanceMeters: 18.3f)
        });
    await PostAsync(http, "v1/telemetry/los", losEnvelope, jsonOptions);

    var shotEnvelope = new TelemetryEnvelope<ShotEvent>(
        options.MatchSessionId,
        "server_bridge_agent",
        DateTimeOffset.UtcNow,
        new[]
        {
            new ShotEvent(
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
                HitSteamId: options.TargetSteamId)
        });
    await PostAsync(http, "v1/telemetry/shots", shotEnvelope, jsonOptions);

    await PrintHealthAsync(http, options, jsonOptions);
    await PrintActionsAsync(http, options, jsonOptions);

    await Task.Delay(TimeSpan.FromSeconds(options.TickIntervalSec));
}

Console.WriteLine("Server bridge simulation complete.");

static TickPlayerState BuildTick(AgentOptions options, long tick)
{
    var speed = options.SimulateCheat ? 480f : 260f;
    return new TickPlayerState(
        options.MatchSessionId,
        tick,
        DateTimeOffset.UtcNow,
        options.AccountId,
        options.SteamId,
        "CT",
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

static async Task PostAsync<T>(
    HttpClient http,
    string url,
    T payload,
    JsonSerializerOptions jsonOptions)
{
    var response = await http.PostAsJsonAsync(url, payload, jsonOptions);
    response.EnsureSuccessStatusCode();
}

static async Task PrintHealthAsync(HttpClient http, AgentOptions options, JsonSerializerOptions jsonOptions)
{
    var health = await http.GetFromJsonAsync<MatchHealthResponse>(
        $"v1/attestation/match-health?matchSessionId={options.MatchSessionId}",
        jsonOptions);

    if (health is null)
    {
        return;
    }

    foreach (var player in health.Players.Where(p => p.AccountId == options.AccountId))
    {
        Console.WriteLine($"Health: account={player.AccountId}, status={player.Status}, action={player.RecommendedAction}");
    }
}

static async Task PrintActionsAsync(HttpClient http, AgentOptions options, JsonSerializerOptions jsonOptions)
{
    var actions = await http.GetFromJsonAsync<List<EnforcementAction>>(
        $"v1/enforcement/actions/{options.MatchSessionId}",
        jsonOptions);
    if (actions is null || actions.Count == 0)
    {
        return;
    }

    foreach (var action in actions.Where(a => a.AccountId == options.AccountId).Take(3))
    {
        Console.WriteLine($"Action: {action.ActionType} ({action.ReasonCode}) at {action.CreatedAtUtc:O}");
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
    public string TargetAccountId { get; private set; } = "acc_enemy";
    public string TargetSteamId { get; private set; } = "76561190000000009";
    public string WeaponId { get; private set; } = "ak47";
    public int RuntimeSec { get; private set; } = 30;
    public int TickIntervalSec { get; private set; } = 2;
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
                case "--simulate-cheat":
                    options.SimulateCheat = true;
                    break;
                case "--runtime-sec":
                    options.RuntimeSec = int.Parse(ReadValue(args, ++i, "--runtime-sec"));
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

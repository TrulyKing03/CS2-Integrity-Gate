using System.Collections.Concurrent;
using Cs2.Plugin.CounterStrikeSharp;
using Microsoft.Extensions.Options;
using Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection(GatewayOptions.SectionName));
builder.Services.AddSingleton<GatewayHostBridge>();
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<GatewayOptions>>().Value;
    return new PluginRuntimeOptions
    {
        BackendBaseUrl = options.BackendBaseUrl,
        ServerApiKey = options.ServerApiKey,
        ExecutorId = options.ExecutorId,
        TelemetrySource = options.TelemetrySource,
        MaxBatchSize = options.MaxBatchSize,
        TelemetryFlushSec = options.TelemetryFlushSec,
        HealthPollSec = options.HealthPollSec,
        ActionPollSec = options.ActionPollSec,
        PendingActionFetchLimit = options.PendingActionFetchLimit
    };
});
builder.Services.AddSingleton<PluginRuntime>(sp =>
{
    var runtimeOptions = sp.GetRequiredService<PluginRuntimeOptions>();
    var hostBridge = sp.GetRequiredService<GatewayHostBridge>();
    return new PluginRuntime(runtimeOptions, hostBridge);
});
builder.Services.AddSingleton<MatchRuntimeCoordinator>(sp =>
{
    var runtime = sp.GetRequiredService<PluginRuntime>();
    var hostBridge = sp.GetRequiredService<GatewayHostBridge>();
    var runtimeOptions = sp.GetRequiredService<PluginRuntimeOptions>();
    return new MatchRuntimeCoordinator(runtime, hostBridge, runtimeOptions);
});

var app = builder.Build();
var gatewayOptions = app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value;
app.Urls.Clear();
app.Urls.Add(gatewayOptions.ListenUrl);

app.MapGet("/", () => Results.Ok(new
{
    service = "plugin-bridge-gateway",
    version = "0.1.0",
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/healthz", () => Results.Ok(new { ok = true, utc = DateTimeOffset.UtcNow }));

app.MapPost("/v1/plugin/connect-attempt", async (
    PlayerConnectionAttempt attempt,
    HttpContext context,
    GatewayHostBridge hostBridge,
    PluginRuntime runtime,
    IOptions<GatewayOptions> options,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureBridgeAuthorized(context, options.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    hostBridge.BeginConnectionDecision(attempt);
    await runtime.HandleConnectionAttemptAsync(attempt, cancellationToken);
    if (!hostBridge.TryTakeConnectionDecision(attempt, out var decision))
    {
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }

    return Results.Ok(decision);
});

app.MapPost("/v1/plugin/connected", (
    PlayerSessionIdentity session,
    HttpContext context,
    MatchRuntimeCoordinator coordinator,
    IOptions<GatewayOptions> options) =>
{
    var authFailure = EnsureBridgeAuthorized(context, options.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    coordinator.TrackPlayer(session);
    return Results.Ok(new { tracked = true, session.MatchSessionId, session.AccountId });
});

app.MapPost("/v1/plugin/disconnected", async (
    PlayerSessionIdentity session,
    HttpContext context,
    MatchRuntimeCoordinator coordinator,
    IOptions<GatewayOptions> options,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureBridgeAuthorized(context, options.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    await coordinator.UntrackPlayerAsync(session);
    _ = cancellationToken;
    return Results.Ok(new { tracked = false, session.MatchSessionId, session.AccountId });
});

app.MapPost("/v1/plugin/ticks", async (
    PluginBatch<TickSample> batch,
    HttpContext context,
    PluginRuntime runtime,
    IOptions<GatewayOptions> options,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureBridgeAuthorized(context, options.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    foreach (var sample in batch.Items)
    {
        await runtime.CaptureTickAsync(sample, cancellationToken);
    }

    return Results.Ok(new { ingested = batch.Items.Count, type = "tick" });
});

app.MapPost("/v1/plugin/shots", async (
    PluginBatch<ShotSample> batch,
    HttpContext context,
    PluginRuntime runtime,
    IOptions<GatewayOptions> options,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureBridgeAuthorized(context, options.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    foreach (var sample in batch.Items)
    {
        await runtime.CaptureShotAsync(sample, cancellationToken);
    }

    return Results.Ok(new { ingested = batch.Items.Count, type = "shot" });
});

app.MapPost("/v1/plugin/visibility", async (
    PluginBatch<VisibilitySample> batch,
    HttpContext context,
    PluginRuntime runtime,
    IOptions<GatewayOptions> options,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureBridgeAuthorized(context, options.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    foreach (var sample in batch.Items)
    {
        await runtime.CaptureVisibilityAsync(sample, cancellationToken);
    }

    return Results.Ok(new { ingested = batch.Items.Count, type = "visibility" });
});

app.MapPost("/v1/plugin/flush", async (
    FlushTelemetryRequest request,
    HttpContext context,
    PluginRuntime runtime,
    IOptions<GatewayOptions> options,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureBridgeAuthorized(context, options.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    await runtime.FlushTelemetryAsync(request.MatchSessionId, cancellationToken);
    return Results.Ok(new { flushed = true, request.MatchSessionId });
});

app.MapPost("/v1/plugin/host-actions/consume", (
    ConsumeHostActionsRequest request,
    HttpContext context,
    GatewayHostBridge hostBridge,
    IOptions<GatewayOptions> options) =>
{
    var authFailure = EnsureBridgeAuthorized(context, options.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var actions = hostBridge.ConsumePendingActions(request.MatchSessionId, request.AccountId);
    return Results.Ok(new { actions });
});

app.MapGet("/v1/plugin/host-actions/count", (
    string matchSessionId,
    string? accountId,
    HttpContext context,
    GatewayHostBridge hostBridge,
    IOptions<GatewayOptions> options) =>
{
    var authFailure = EnsureBridgeAuthorized(context, options.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var count = hostBridge.CountPendingActions(matchSessionId, accountId);
    return Results.Ok(new { matchSessionId, accountId, count });
});

app.MapGet("/v1/plugin/metrics", (
    HttpContext context,
    GatewayHostBridge hostBridge,
    IOptions<GatewayOptions> options) =>
{
    var authFailure = EnsureBridgeAuthorized(context, options.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    return Results.Ok(hostBridge.GetMetrics());
});

var coordinatorForShutdown = app.Services.GetRequiredService<MatchRuntimeCoordinator>();
var runtimeForShutdown = app.Services.GetRequiredService<PluginRuntime>();
app.Lifetime.ApplicationStopping.Register(() =>
{
    coordinatorForShutdown.DisposeAsync().AsTask().GetAwaiter().GetResult();
    runtimeForShutdown.Dispose();
});

app.Run();

static IResult? EnsureBridgeAuthorized(HttpContext context, GatewayOptions options)
{
    if (!context.Request.Headers.TryGetValue("X-Bridge-Api-Key", out var headerValues))
    {
        return Results.Unauthorized();
    }

    var provided = headerValues.ToString();
    if (!string.Equals(provided, options.BridgeApiKey, StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

    return null;
}

internal sealed record PluginBatch<T>(IReadOnlyList<T> Items);
internal sealed record FlushTelemetryRequest(string MatchSessionId);
internal sealed record ConsumeHostActionsRequest(string MatchSessionId, string? AccountId);
internal sealed record ConnectionDecisionResponse(bool Allow, string Reason);

internal sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    public string ListenUrl { get; set; } = "http://localhost:5055";
    public string BridgeApiKey { get; set; } =
        Environment.GetEnvironmentVariable("CS2IG_BRIDGE_API_KEY") ?? "dev-bridge-api-key";
    public string BackendBaseUrl { get; set; } = "http://localhost:5042";
    public string ServerApiKey { get; set; } =
        Environment.GetEnvironmentVariable("CS2IG_SERVER_API_KEY") ?? "dev-server-api-key";
    public string ExecutorId { get; set; } = "plugin-bridge-gateway";
    public string TelemetrySource { get; set; } = "plugin_bridge_gateway";
    public int MaxBatchSize { get; set; } = 64;
    public int TelemetryFlushSec { get; set; } = 3;
    public int HealthPollSec { get; set; } = 5;
    public int ActionPollSec { get; set; } = 3;
    public int PendingActionFetchLimit { get; set; } = 200;
    public int HostActionTtlSec { get; set; } = 180;
}

internal sealed class GatewayHostBridge(
    ILogger<GatewayHostBridge> logger,
    IOptions<GatewayOptions> options) : IPluginHostBridge
{
    private readonly ConcurrentDictionary<string, ConnectionDecisionResponse> _connectionDecisions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<QueuedHostAction>> _pendingActions = new(StringComparer.Ordinal);
    private readonly TimeSpan _hostActionTtl = TimeSpan.FromSeconds(Math.Max(5, options.Value.HostActionTtlSec));

    public void BeginConnectionDecision(PlayerConnectionAttempt attempt)
    {
        _connectionDecisions.TryRemove(ConnectionKey(attempt), out _);
    }

    public bool TryTakeConnectionDecision(PlayerConnectionAttempt attempt, out ConnectionDecisionResponse decision)
    {
        return _connectionDecisions.TryRemove(ConnectionKey(attempt), out decision!);
    }

    public IReadOnlyList<EnforcementAction> ConsumePendingActions(string matchSessionId, string? accountId)
    {
        if (!_pendingActions.TryGetValue(matchSessionId, out var queue))
        {
            return Array.Empty<EnforcementAction>();
        }

        CleanupExpired(queue);
        var consumed = new List<EnforcementAction>();
        var keep = new List<QueuedHostAction>();
        while (queue.TryDequeue(out var queued))
        {
            var action = queued.Action;
            var match = string.IsNullOrWhiteSpace(accountId) ||
                        string.Equals(action.AccountId, accountId, StringComparison.Ordinal);
            if (match)
            {
                consumed.Add(action);
            }
            else
            {
                keep.Add(queued);
            }
        }

        foreach (var queued in keep)
        {
            queue.Enqueue(queued);
        }

        return consumed;
    }

    public int CountPendingActions(string matchSessionId, string? accountId)
    {
        if (!_pendingActions.TryGetValue(matchSessionId, out var queue))
        {
            return 0;
        }

        CleanupExpired(queue);
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return queue.Count;
        }

        return queue.Count(queued => string.Equals(queued.Action.AccountId, accountId, StringComparison.Ordinal));
    }

    public object GetMetrics()
    {
        var pendingPerMatch = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (var kvp in _pendingActions)
        {
            CleanupExpired(kvp.Value);
            var count = kvp.Value.Count;
            pendingPerMatch[kvp.Key] = count;
            total += count;
        }

        return new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            pendingActionTotal = total,
            pendingActionByMatch = pendingPerMatch,
            connectionDecisionBacklog = _connectionDecisions.Count
        };
    }

    public Task DenyConnectionAsync(PlayerConnectionAttempt attempt, string reason, CancellationToken cancellationToken)
    {
        _connectionDecisions[ConnectionKey(attempt)] = new ConnectionDecisionResponse(false, reason);
        LogWarning($"DenyConnection account={attempt.AccountId} match={attempt.MatchSessionId} reason={reason}");
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    public Task AcceptConnectionAsync(PlayerConnectionAttempt attempt, CancellationToken cancellationToken)
    {
        _connectionDecisions[ConnectionKey(attempt)] = new ConnectionDecisionResponse(true, "ok");
        LogInfo($"AcceptConnection account={attempt.AccountId} match={attempt.MatchSessionId}");
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    public Task ApplyEnforcementActionAsync(EnforcementAction action, CancellationToken cancellationToken)
    {
        var queue = _pendingActions.GetOrAdd(action.MatchSessionId, _ => new ConcurrentQueue<QueuedHostAction>());
        queue.Enqueue(new QueuedHostAction(action, DateTimeOffset.UtcNow));
        LogWarning(
            $"QueuedHostAction actionId={action.ActionId} match={action.MatchSessionId} account={action.AccountId} reason={action.ReasonCode}");
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    public void LogInfo(string message) => logger.LogInformation("{Message}", message);
    public void LogWarning(string message) => logger.LogWarning("{Message}", message);
    public void LogError(string message, Exception? exception = null) => logger.LogError(exception, "{Message}", message);

    private static string ConnectionKey(PlayerConnectionAttempt attempt)
    {
        return $"{attempt.MatchSessionId}|{attempt.AccountId}|{attempt.SteamId}|{attempt.JoinToken}";
    }

    private void CleanupExpired(ConcurrentQueue<QueuedHostAction> queue)
    {
        var now = DateTimeOffset.UtcNow;
        var keep = new List<QueuedHostAction>();
        while (queue.TryDequeue(out var queued))
        {
            if (now - queued.QueuedAtUtc <= _hostActionTtl)
            {
                keep.Add(queued);
            }
        }

        foreach (var queued in keep)
        {
            queue.Enqueue(queued);
        }
    }

    private sealed record QueuedHostAction(EnforcementAction Action, DateTimeOffset QueuedAtUtc);
}

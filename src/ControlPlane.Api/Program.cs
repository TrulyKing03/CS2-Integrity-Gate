using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlPlane.Api.Options;
using ControlPlane.Api.Persistence;
using ControlPlane.Api.Security;
using ControlPlane.Api.Services;
using Microsoft.Extensions.Options;
using Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<AcPolicyOptions>(builder.Configuration.GetSection(AcPolicyOptions.SectionName));
builder.Services.Configure<ApiAuthOptions>(builder.Configuration.GetSection(ApiAuthOptions.SectionName));
builder.Services.Configure<EvidenceOptions>(builder.Configuration.GetSection(EvidenceOptions.SectionName));
builder.Services.AddSingleton<ISqliteStore, SqliteStore>();
builder.Services.AddSingleton<IJoinTokenService, JoinTokenService>();
builder.Services.AddSingleton<IDetectionEngine, DetectionEngine>();
builder.Services.AddSingleton<IEvidenceService, EvidenceService>();
builder.Services.AddHostedService<StartupInitializer>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "cs2-control-plane",
    version = "0.1.0",
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/healthz", () => Results.Ok(new { ok = true, utc = DateTimeOffset.UtcNow }));

app.MapGet("/v1/policy/current", (
    IOptions<AcPolicyOptions> policyOptions) =>
{
    var policy = policyOptions.Value;
    return Results.Ok(new
    {
        policyVersion = policy.PolicyVersion,
        defaultQueueTier = policy.DefaultQueueTier,
        heartbeatIntervalSec = policy.HeartbeatIntervalSec,
        graceWindowSec = policy.GraceWindowSec,
        requiredTierA = policy.RequiredTierA,
        requiredTierB = policy.RequiredTierB
    });
});

app.MapPost("/v1/auth/login", async (
    LoginRequest request,
    ISqliteStore store,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Username))
    {
        return Results.BadRequest(new { error = "username_required" });
    }

    var accountId = CreateAccountId(request.Username);
    var steamId = CreateSteamId(request.Username);
    await store.EnsureAccountAsync(accountId, steamId, cancellationToken);

    return Results.Ok(new LoginResponse(
        accountId,
        AccessToken(),
        steamId,
        SteamLinked: true));
});

app.MapPost("/v1/queue/enqueue", async (
    QueueRequest request,
    ISqliteStore store,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.AccountId) || string.IsNullOrWhiteSpace(request.SteamId))
    {
        return Results.BadRequest(new { error = "account_and_steam_required" });
    }

    await store.EnsureAccountAsync(request.AccountId, request.SteamId, cancellationToken);
    var queueType = NormalizeQueueType(request.QueueType);
    var queueResponse = new QueueResponse(
        MatchSessionId(),
        ServerId(),
        "eu-central",
        queueType,
        DateTimeOffset.UtcNow.AddSeconds(5));
    await store.CreateMatchSessionAsync(queueResponse, request.AccountId, request.SteamId, cancellationToken);

    return Results.Ok(queueResponse);
});

app.MapPost("/v1/attestation/enroll", async (
    EnrollRequest request,
    ISqliteStore store,
    IOptions<AcPolicyOptions> options,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.AccountId) ||
        string.IsNullOrWhiteSpace(request.SteamId) ||
        string.IsNullOrWhiteSpace(request.DevicePublicKeyPem))
    {
        return Results.BadRequest(new { error = "invalid_enroll_payload" });
    }

    await store.EnsureAccountAsync(request.AccountId, request.SteamId, cancellationToken);
    var deviceId = DeviceId(request.AccountId, request.DevicePublicKeyPem);
    await store.UpsertDeviceAsync(
        deviceId,
        request.AccountId,
        request.SteamId,
        request.DevicePublicKeyPem,
        status: "enrolled",
        cancellationToken);

    var response = new EnrollResponse(
        deviceId,
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        options.Value.PolicyVersion,
        "A");
    return Results.Ok(response);
});

app.MapPost("/v1/attestation/match-start", async (
    MatchStartRequest request,
    ISqliteStore store,
    IOptions<AcPolicyOptions> policyOptions,
    IJoinTokenService tokenService,
    CancellationToken cancellationToken) =>
{
    var isMatchPlayer = await store.IsMatchPlayerAsync(
        request.MatchSessionId,
        request.AccountId,
        request.SteamId,
        cancellationToken);
    if (!isMatchPlayer)
    {
        return Results.BadRequest(new { error = "not_in_match_session" });
    }

    if (!request.PlatformSignals.SecureBoot || !request.PlatformSignals.Tpm20)
    {
        return Results.BadRequest(new { error = "tier_a_requirements_not_met" });
    }

    var queueType = await store.GetQueueTypeForMatchAsync(request.MatchSessionId, cancellationToken) ?? "standard";
    if (string.Equals(queueType, "high_trust", StringComparison.OrdinalIgnoreCase))
    {
        if (policyOptions.Value.RequiredTierB.Iommu && !request.PlatformSignals.Iommu)
        {
            return Results.BadRequest(new { error = "tier_b_iommu_required_for_high_trust" });
        }

        if (policyOptions.Value.RequiredTierB.Vbs && !request.PlatformSignals.Vbs)
        {
            return Results.BadRequest(new { error = "tier_b_vbs_required_for_high_trust" });
        }
    }

    if (!request.IntegritySignals.AcServiceHealthy ||
        !request.IntegritySignals.ModulePolicyOk ||
        !request.IntegritySignals.DriverLoaded)
    {
        return Results.BadRequest(new { error = "integrity_signal_failed" });
    }

    var serverId = await store.GetServerIdForMatchAsync(request.MatchSessionId, cancellationToken);
    if (string.IsNullOrWhiteSpace(serverId))
    {
        return Results.BadRequest(new { error = "match_server_not_found" });
    }

    var issuedAt = DateTimeOffset.UtcNow;
    var expiresAt = issuedAt.AddSeconds(policyOptions.Value.JoinTokenTtlSec);
    var payload = new JoinTokenPayload(
        Guid.NewGuid().ToString("N"),
        request.AccountId,
        request.SteamId,
        request.MatchSessionId,
        serverId,
        request.DeviceId,
        issuedAt,
        expiresAt);

    var joinToken = tokenService.Issue(payload);
    await store.SaveJoinTokenAsync(payload, cancellationToken);
    await store.UpsertPlayerHealthAsync(
        request.MatchSessionId,
        request.AccountId,
        request.SteamId,
        "healthy",
        DateTimeOffset.UtcNow,
        "none",
        cancellationToken);

    return Results.Ok(new MatchStartResponse(
        joinToken,
        policyOptions.Value.JoinTokenTtlSec,
        policyOptions.Value.HeartbeatIntervalSec,
        policyOptions.Value.GraceWindowSec,
        queueType));
});

app.MapPost("/v1/attestation/heartbeat", async (
    HeartbeatRequest request,
    ISqliteStore store,
    IOptions<AcPolicyOptions> policyOptions,
    CancellationToken cancellationToken) =>
{
    var steamId = await store.GetSteamIdForMatchPlayerAsync(request.MatchSessionId, request.AccountId, cancellationToken);
    if (string.IsNullOrWhiteSpace(steamId))
    {
        return Results.BadRequest(new { error = "unknown_match_player" });
    }

    var healthy =
        request.PlatformSignals.SecureBoot &&
        request.PlatformSignals.Tpm20 &&
        request.IntegritySignals.AcServiceHealthy &&
        request.IntegritySignals.AntiTamperOk &&
        request.IntegritySignals.ModulePolicyOk &&
        request.IntegritySignals.DriverLoaded;

    var status = healthy ? "healthy" : "unhealthy";
    var actionHint = healthy ? "none" : "kick";
    var now = DateTimeOffset.UtcNow;

    await store.AddHeartbeatAsync(request, steamId, status, cancellationToken);
    await store.UpsertPlayerHealthAsync(
        request.MatchSessionId,
        request.AccountId,
        steamId,
        status,
        now,
        actionHint,
        cancellationToken);

    return Results.Ok(new HeartbeatResponse(
        status,
        policyOptions.Value.HeartbeatIntervalSec,
        actionHint));
});

app.MapPost("/v1/attestation/validate-join", async (
    ValidateJoinRequest request,
    HttpContext context,
    ISqliteStore store,
    IJoinTokenService tokenService,
    IOptions<ApiAuthOptions> apiAuthOptions,
    IOptions<AcPolicyOptions> policyOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureServerAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    if (!tokenService.TryValidate(request.JoinToken, out var payload, out var reason) || payload is null)
    {
        return Results.Ok(new ValidateJoinResponse(false, reason ?? "invalid_token", "unknown", "unknown"));
    }

    if (!string.Equals(payload.MatchSessionId, request.MatchSessionId, StringComparison.Ordinal) ||
        !string.Equals(payload.AccountId, request.AccountId, StringComparison.Ordinal) ||
        !string.Equals(payload.SteamId, request.SteamId, StringComparison.Ordinal) ||
        !string.Equals(payload.ServerId, request.ServerId, StringComparison.Ordinal))
    {
        return Results.Ok(new ValidateJoinResponse(false, "token_claim_mismatch", "unknown", "unknown"));
    }

    var tokenRecord = await store.GetJoinTokenAsync(payload.Jti, cancellationToken);
    if (tokenRecord is null)
    {
        return Results.Ok(new ValidateJoinResponse(false, "token_not_issued", "unknown", "unknown"));
    }

    if (tokenRecord.UsedAtUtc is not null)
    {
        return Results.Ok(new ValidateJoinResponse(false, "token_replayed", "unknown", "unknown"));
    }

    var latestHeartbeat = await store.GetLatestHeartbeatAsync(request.MatchSessionId, request.AccountId, cancellationToken);
    if (latestHeartbeat is null)
    {
        return Results.Ok(new ValidateJoinResponse(false, "missing_heartbeat", "unhealthy", "unknown"));
    }

    var staleCutoff = DateTimeOffset.UtcNow.AddSeconds(-policyOptions.Value.GraceWindowSec);
    if (latestHeartbeat.ReceivedAtUtc < staleCutoff)
    {
        return Results.Ok(new ValidateJoinResponse(false, "stale_heartbeat", "unhealthy", "unknown"));
    }

    if (!string.Equals(latestHeartbeat.Status, "healthy", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok(new ValidateJoinResponse(false, "heartbeat_unhealthy", "unhealthy", "unknown"));
    }

    await store.MarkJoinTokenUsedAsync(payload.Jti, cancellationToken);
    return Results.Ok(new ValidateJoinResponse(true, "ok", "healthy", "high_trust"));
});

app.MapGet("/v1/attestation/match-health", async (
    string matchSessionId,
    HttpContext context,
    ISqliteStore store,
    IOptions<ApiAuthOptions> apiAuthOptions,
    IOptions<AcPolicyOptions> options,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureServerAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var rows = await store.GetMatchHealthAsync(matchSessionId, cancellationToken);
    var staleCutoff = DateTimeOffset.UtcNow.AddSeconds(-options.Value.GraceWindowSec);
    var normalized = rows
        .Select(player =>
        {
            if (player.LastHeartbeatUtc < staleCutoff)
            {
                return player with { Status = "unhealthy", RecommendedAction = "kick" };
            }

            return player;
        })
        .ToArray();

    return Results.Ok(new MatchHealthResponse(matchSessionId, DateTimeOffset.UtcNow, normalized));
});

app.MapPost("/v1/telemetry/ticks", async (
    TelemetryEnvelope<TickPlayerState> envelope,
    HttpContext context,
    ISqliteStore store,
    IDetectionEngine detectionEngine,
    IEvidenceService evidenceService,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureServerAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    await store.AddTelemetryEventsAsync(
        envelope.MatchSessionId,
        "tick_player_state_v1",
        envelope.Items,
        tick => tick.AccountId,
        tick => tick.TickId,
        cancellationToken);

    var result = detectionEngine.ProcessTicks(envelope);
    await PersistDetectionResultAsync(result, envelope.MatchSessionId, store, evidenceService, cancellationToken);
    return Results.Ok(new { ingested = envelope.Items.Count, scores = result.ScoreUpdates.Count, actions = result.EnforcementActions.Count });
});

app.MapPost("/v1/telemetry/shots", async (
    TelemetryEnvelope<ShotEvent> envelope,
    HttpContext context,
    ISqliteStore store,
    IDetectionEngine detectionEngine,
    IEvidenceService evidenceService,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureServerAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    await store.AddTelemetryEventsAsync(
        envelope.MatchSessionId,
        "shot_event_v1",
        envelope.Items,
        shot => shot.ShooterAccountId,
        shot => shot.TickId,
        cancellationToken);

    var result = detectionEngine.ProcessShots(envelope);
    await PersistDetectionResultAsync(result, envelope.MatchSessionId, store, evidenceService, cancellationToken);
    return Results.Ok(new { ingested = envelope.Items.Count, scores = result.ScoreUpdates.Count, actions = result.EnforcementActions.Count });
});

app.MapPost("/v1/telemetry/los", async (
    TelemetryEnvelope<LosSample> envelope,
    HttpContext context,
    ISqliteStore store,
    IDetectionEngine detectionEngine,
    IEvidenceService evidenceService,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureServerAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    await store.AddTelemetryEventsAsync(
        envelope.MatchSessionId,
        "los_sample_v1",
        envelope.Items,
        los => los.ObserverAccountId,
        los => los.TickId,
        cancellationToken);

    var result = detectionEngine.ProcessLosSamples(envelope);
    await PersistDetectionResultAsync(result, envelope.MatchSessionId, store, evidenceService, cancellationToken);
    return Results.Ok(new { ingested = envelope.Items.Count });
});

app.MapGet("/v1/detections/scores/{matchSessionId}/{accountId}", async (
    string matchSessionId,
    string accountId,
    ISqliteStore store,
    CancellationToken cancellationToken) =>
{
    var scores = await store.GetSuspicionScoresAsync(matchSessionId, accountId, cancellationToken);
    return Results.Ok(scores);
});

app.MapGet("/v1/enforcement/actions/{matchSessionId}", async (
    string matchSessionId,
    HttpContext context,
    ISqliteStore store,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureServerAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var actions = await store.GetEnforcementActionsAsync(matchSessionId, cancellationToken);
    return Results.Ok(actions);
});

app.MapGet("/v1/enforcement/actions/{matchSessionId}/pending", async (
    string matchSessionId,
    string? accountId,
    HttpContext context,
    ISqliteStore store,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureServerAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var actions = await store.GetPendingEnforcementActionsAsync(matchSessionId, accountId, cancellationToken);
    return Results.Ok(actions);
});

app.MapPost("/v1/enforcement/actions/ack", async (
    EnforcementActionAckRequest request,
    HttpContext context,
    ISqliteStore store,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureServerAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var accepted = await store.AcknowledgeEnforcementActionAsync(request, cancellationToken);
    var reason = accepted ? "accepted" : "already_acked";
    return Results.Ok(new EnforcementActionAckResponse(accepted, reason, DateTimeOffset.UtcNow));
});

app.MapGet("/v1/evidence", async (
    string? matchSessionId,
    string? accountId,
    HttpContext context,
    ISqliteStore store,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureInternalAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var evidence = await store.ListEvidencePackSummariesAsync(matchSessionId, accountId, cancellationToken);
    return Results.Ok(evidence);
});

app.MapGet("/v1/evidence/{evidenceId}", async (
    string evidenceId,
    HttpContext context,
    ISqliteStore store,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureInternalAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var evidence = await store.GetEvidencePackSummaryAsync(evidenceId, cancellationToken);
    return evidence is null
        ? Results.NotFound(new { error = "evidence_not_found" })
        : Results.Ok(evidence);
});

app.MapPost("/v1/review/cases", async (
    CreateReviewCaseRequest request,
    HttpContext context,
    ISqliteStore store,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureInternalAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var created = await store.CreateReviewCaseAsync(request, cancellationToken);
    return Results.Ok(created);
});

app.MapGet("/v1/review/cases", async (
    string? status,
    HttpContext context,
    ISqliteStore store,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureInternalAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var cases = await store.ListReviewCasesAsync(status, cancellationToken);
    return Results.Ok(cases);
});

app.MapPost("/v1/review/cases/update", async (
    UpdateReviewCaseRequest request,
    HttpContext context,
    ISqliteStore store,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureInternalAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var updated = await store.UpdateReviewCaseAsync(request, cancellationToken);
    return updated is null
        ? Results.NotFound(new { error = "review_case_not_found" })
        : Results.Ok(updated);
});

app.MapPost("/v1/moderation/bans", async (
    CreateBanRequest request,
    HttpContext context,
    ISqliteStore store,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureInternalAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var ban = await store.CreateBanAsync(request, cancellationToken);
    return Results.Ok(ban);
});

app.MapGet("/v1/moderation/bans/{banId}", async (
    string banId,
    HttpContext context,
    ISqliteStore store,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureInternalAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var ban = await store.GetBanAsync(banId, cancellationToken);
    return ban is null
        ? Results.NotFound(new { error = "ban_not_found" })
        : Results.Ok(ban);
});

app.MapPost("/v1/moderation/appeals", async (
    CreateAppealRequest request,
    HttpContext context,
    ISqliteStore store,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureInternalAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var appeal = await store.CreateAppealAsync(request, cancellationToken);
    return Results.Ok(appeal);
});

app.MapGet("/v1/moderation/appeals", async (
    string? status,
    HttpContext context,
    ISqliteStore store,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureInternalAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var appeals = await store.ListAppealsAsync(status, cancellationToken);
    return Results.Ok(appeals);
});

app.MapPost("/v1/moderation/appeals/resolve", async (
    ResolveAppealRequest request,
    HttpContext context,
    ISqliteStore store,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureInternalAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var updated = await store.ResolveAppealAsync(request, cancellationToken);
    return updated is null
        ? Results.NotFound(new { error = "appeal_not_found" })
        : Results.Ok(updated);
});

app.Run();

static async Task PersistDetectionResultAsync(
    DetectionResult result,
    string matchSessionId,
    ISqliteStore store,
    IEvidenceService evidenceService,
    CancellationToken cancellationToken)
{
    foreach (var update in result.ScoreUpdates)
    {
        await store.UpsertSuspicionScoreAsync(update, cancellationToken);
    }

    foreach (var action in result.EnforcementActions)
    {
        await store.AddEnforcementActionAsync(
            action,
            $$"""
            {
              "source": "detection_engine",
              "reasonCode": "{{action.ReasonCode}}",
              "createdAtUtc": "{{action.CreatedAtUtc:O}}"
            }
            """,
            cancellationToken);

        await evidenceService.BuildAndStoreEvidenceAsync(
            matchSessionId,
            action.AccountId,
            triggerType: action.ReasonCode,
            actionId: action.ActionId,
            cancellationToken);
    }
}

static string CreateAccountId(string username)
{
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(username.Trim().ToLowerInvariant()));
    return $"acc_{Convert.ToHexString(hash).ToLowerInvariant()[..12]}";
}

static string CreateSteamId(string username)
{
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"steam::{username.Trim().ToLowerInvariant()}"));
    var value = BitConverter.ToUInt64(hash, 0) % 9_999_999_999UL;
    return $"7656{value:0000000000}";
}

static string AccessToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

static string MatchSessionId() => $"ms_{Guid.NewGuid():N}";
static string ServerId() => $"srv_eu_{Random.Shared.Next(1, 16):00}";

static string DeviceId(string accountId, string devicePublicKeyPem)
{
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{accountId}:{devicePublicKeyPem}"));
    return $"did_{Convert.ToHexString(hash).ToLowerInvariant()[..16]}";
}

static string NormalizeQueueType(string queueType)
{
    if (string.IsNullOrWhiteSpace(queueType))
    {
        return "high_trust";
    }

    var normalized = queueType.Trim().ToLowerInvariant();
    return normalized is "high_trust" or "standard" ? normalized : "high_trust";
}

static IResult? EnsureServerAuthorized(HttpContext context, ApiAuthOptions options)
{
    if (!context.Request.Headers.TryGetValue("X-Server-Api-Key", out var provided) ||
        string.IsNullOrWhiteSpace(provided) ||
        !string.Equals(provided.ToString(), options.ServerApiKey, StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

    return null;
}

static IResult? EnsureInternalAuthorized(HttpContext context, ApiAuthOptions options)
{
    if (!context.Request.Headers.TryGetValue("X-Internal-Api-Key", out var provided) ||
        string.IsNullOrWhiteSpace(provided) ||
        !string.Equals(provided.ToString(), options.InternalApiKey, StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

    return null;
}

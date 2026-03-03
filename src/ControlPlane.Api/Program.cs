using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using ControlPlane.Api.Options;
using ControlPlane.Api.Persistence;
using ControlPlane.Api.Security;
using ControlPlane.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<AcPolicyOptions>(builder.Configuration.GetSection(AcPolicyOptions.SectionName));
builder.Services.Configure<ApiAuthOptions>(builder.Configuration.GetSection(ApiAuthOptions.SectionName));
builder.Services.Configure<ApiRateLimitOptions>(builder.Configuration.GetSection(ApiRateLimitOptions.SectionName));
builder.Services.Configure<EvidenceOptions>(builder.Configuration.GetSection(EvidenceOptions.SectionName));
builder.Services.Configure<DataRetentionOptions>(builder.Configuration.GetSection(DataRetentionOptions.SectionName));
builder.Services.AddSingleton<ISqliteStore, SqliteStore>();
builder.Services.AddSingleton<IJoinTokenService, JoinTokenService>();
builder.Services.AddSingleton<IDetectionEngine, DetectionEngine>();
builder.Services.AddSingleton<IEvidenceService, EvidenceService>();
builder.Services.AddSingleton<RetentionCleanupState>();
builder.Services.AddHostedService<StartupInitializer>();
builder.Services.AddHostedService<RetentionCleanupWorker>();

var rateLimitOptions =
    builder.Configuration.GetSection(ApiRateLimitOptions.SectionName).Get<ApiRateLimitOptions>() ??
    new ApiRateLimitOptions();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        var httpContext = context.HttpContext;
        var store = httpContext.RequestServices.GetService<ISqliteStore>();
        if (store is not null)
        {
            await TryWriteSecurityEventAsync(
                store,
                eventType: "rate_limited",
                severity: "medium",
                source: "rate_limiter",
                accountId: null,
                matchSessionId: null,
                ipAddress: ReadRemoteAddress(httpContext),
                details: new
                {
                    route = httpContext.Request.Path.Value ?? "/",
                    method = httpContext.Request.Method
                },
                cancellationToken);
        }

        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "rate_limited" },
            cancellationToken: cancellationToken);
    };

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var path = httpContext.Request.Path.Value ?? "/";
        var policyName = ResolveRateLimitPolicy(path);
        return policyName switch
        {
            "public_auth" => CreatePartition(
                $"public_auth:{ReadRemoteAddress(httpContext)}",
                rateLimitOptions.PublicAuth),
            "public_client" => CreatePartition(
                $"public_client:{ReadRemoteAddress(httpContext)}",
                rateLimitOptions.PublicClient),
            "server_api" => CreatePartition(
                $"server_api:{ReadHeaderOrFallback(httpContext, "X-Server-Api-Key")}",
                rateLimitOptions.ServerApi),
            "internal_api" => CreatePartition(
                $"internal_api:{ReadHeaderOrFallback(httpContext, "X-Internal-Api-Key")}",
                rateLimitOptions.InternalApi),
            _ => RateLimitPartition.GetNoLimiter("system")
        };
    });
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var app = builder.Build();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    await next();

    if (context.Response.StatusCode != StatusCodes.Status401Unauthorized)
    {
        return;
    }

    var store = context.RequestServices.GetService<ISqliteStore>();
    if (store is null)
    {
        return;
    }

    await TryWriteSecurityEventAsync(
        store,
        eventType: "unauthorized_response",
        severity: "medium",
        source: "http_pipeline",
        accountId: null,
        matchSessionId: null,
        ipAddress: ReadRemoteAddress(context),
        details: new
        {
            route = context.Request.Path.Value ?? "/",
            method = context.Request.Method
        },
        context.RequestAborted);
});

app.MapGet("/", () => Results.Ok(new
{
    service = "cs2-control-plane",
    version = "0.1.0",
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/healthz", () => Results.Ok(new { ok = true, utc = DateTimeOffset.UtcNow }));

app.MapGet("/v1/policy/current", (
    IOptions<AcPolicyOptions> policyOptions,
    IOptions<ApiRateLimitOptions> rateLimitPolicy) =>
{
    var policy = policyOptions.Value;
    var rateLimits = rateLimitPolicy.Value;
    return Results.Ok(new
    {
        policyVersion = policy.PolicyVersion,
        defaultQueueTier = policy.DefaultQueueTier,
        heartbeatIntervalSec = policy.HeartbeatIntervalSec,
        graceWindowSec = policy.GraceWindowSec,
        requireAntiTamperOnMatchStart = policy.RequireAntiTamperOnMatchStart,
        requiredPolicyHashes = policy.RequiredPolicyHashes,
        requiredTierA = policy.RequiredTierA,
        requiredTierB = policy.RequiredTierB,
        detection = policy.Detection,
        apiRateLimit = rateLimits
    });
});

app.MapGet("/v1/metrics/summary", async (
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

    var summary = await store.GetSystemSummaryMetricsAsync(cancellationToken);
    return Results.Ok(summary);
});

app.MapPost("/v1/ops/cleanup/run", async (
    HttpContext context,
    ISqliteStore store,
    RetentionCleanupState state,
    IOptions<ApiAuthOptions> apiAuthOptions,
    IOptions<DataRetentionOptions> retentionOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureInternalAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var options = retentionOptions.Value;
    var result = await store.CleanupExpiredOperationalDataAsync(
        DateTimeOffset.UtcNow,
        TimeSpan.FromMinutes(Math.Max(1, options.JoinTokenRetentionMinutes)),
        TimeSpan.FromHours(Math.Max(1, options.HeartbeatRetentionHours)),
        TimeSpan.FromHours(Math.Max(1, options.SecurityEventRetentionHours)),
        TimeSpan.FromHours(Math.Max(1, options.TelemetryRetentionHours)),
        cancellationToken);
    state.SetSuccess(result);
    return Results.Ok(result);
});

app.MapGet("/v1/ops/cleanup/status", (
    HttpContext context,
    RetentionCleanupState state,
    IOptions<ApiAuthOptions> apiAuthOptions) =>
{
    var authFailure = EnsureInternalAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    return Results.Ok(state.Snapshot());
});

app.MapGet("/v1/ops/security/events", async (
    HttpContext context,
    int? sinceMinutes,
    string? severity,
    string? eventType,
    int? limit,
    ISqliteStore store,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureInternalAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var sinceUtc = sinceMinutes is > 0
        ? DateTimeOffset.UtcNow.AddMinutes(-sinceMinutes.Value)
        : (DateTimeOffset?)null;
    var events = await store.ListSecurityEventsAsync(
        sinceUtc,
        severity,
        eventType,
        limit ?? 200,
        cancellationToken);
    return Results.Ok(events);
});

app.MapGet("/v1/ops/security/summary", async (
    HttpContext context,
    int? sinceMinutes,
    ISqliteStore store,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    var authFailure = EnsureInternalAuthorized(context, apiAuthOptions.Value);
    if (authFailure is not null)
    {
        return authFailure;
    }

    var sinceUtc = sinceMinutes is > 0
        ? DateTimeOffset.UtcNow.AddMinutes(-sinceMinutes.Value)
        : (DateTimeOffset?)null;
    var summary = await store.GetSecurityEventSummaryAsync(sinceUtc, cancellationToken);
    return Results.Ok(summary);
});

app.MapPost("/v1/auth/login", async (
    LoginRequest request,
    ISqliteStore store,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Username))
    {
        return Results.BadRequest(new { error = "username_required" });
    }

    var accountId = CreateAccountId(request.Username);
    var steamId = CreateSteamId(request.Username);
    await store.EnsureAccountAsync(accountId, steamId, cancellationToken);

    var issuedAt = DateTimeOffset.UtcNow;
    var accessToken = AccessToken();
    var tokenHash = HashAccessToken(accessToken);
    await store.UpsertAccountSessionAsync(
        sessionId: $"as_{Guid.NewGuid():N}",
        accountId,
        tokenHash,
        issuedAt,
        issuedAt.AddMinutes(Math.Max(1, apiAuthOptions.Value.AccessTokenTtlMinutes)),
        cancellationToken);

    return Results.Ok(new LoginResponse(
        accountId,
        accessToken,
        steamId,
        SteamLinked: true));
});

app.MapPost("/v1/queue/enqueue", async (
    QueueRequest request,
    HttpContext context,
    ISqliteStore store,
    IOptions<ApiAuthOptions> apiAuthOptions,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.AccountId) || string.IsNullOrWhiteSpace(request.SteamId))
    {
        return Results.BadRequest(new { error = "account_and_steam_required" });
    }

    var token = TryReadBearerToken(context);
    if (apiAuthOptions.Value.RequireQueueAccessToken || !string.IsNullOrWhiteSpace(token))
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            await TryWriteSecurityEventAsync(
                store,
                eventType: "queue_access_denied",
                severity: "medium",
                source: "queue",
                accountId: request.AccountId,
                matchSessionId: null,
                ipAddress: ReadRemoteAddress(context),
                details: new { reason = "missing_access_token" },
                cancellationToken);
            return Results.Unauthorized();
        }

        var tokenHash = HashAccessToken(token);
        var valid = await store.IsValidAccountSessionAsync(
            tokenHash,
            request.AccountId,
            DateTimeOffset.UtcNow,
            cancellationToken);
        if (!valid)
        {
            await TryWriteSecurityEventAsync(
                store,
                eventType: "queue_access_denied",
                severity: "medium",
                source: "queue",
                accountId: request.AccountId,
                matchSessionId: null,
                ipAddress: ReadRemoteAddress(context),
                details: new { reason = "invalid_access_token" },
                cancellationToken);
            return Results.Unauthorized();
        }
    }

    await store.EnsureAccountAsync(request.AccountId, request.SteamId, cancellationToken);
    var activeBan = await store.GetActiveBanForAccountAsync(request.AccountId, cancellationToken);
    if (activeBan is not null)
    {
        await TryWriteSecurityEventAsync(
            store,
            eventType: "queue_banned_account_attempt",
            severity: "high",
            source: "queue",
            accountId: request.AccountId,
            matchSessionId: null,
            ipAddress: ReadRemoteAddress(context),
            details: new { activeBan.BanId, activeBan.Scope, activeBan.Status },
            cancellationToken);
        return Results.BadRequest(new
        {
            error = "account_banned",
            activeBan.BanId,
            activeBan.Scope,
            activeBan.Status,
            activeBan.EndAtUtc
        });
    }

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

    var activeBan = await store.GetActiveBanForAccountAsync(request.AccountId, cancellationToken);
    if (activeBan is not null)
    {
        await TryWriteSecurityEventAsync(
            store,
            eventType: "match_start_banned_account",
            severity: "high",
            source: "attestation.match_start",
            accountId: request.AccountId,
            matchSessionId: request.MatchSessionId,
            ipAddress: null,
            details: new { activeBan.BanId, activeBan.Scope, activeBan.Status },
            cancellationToken);
        return Results.BadRequest(new
        {
            error = "account_banned",
            activeBan.BanId,
            activeBan.Scope,
            activeBan.Status,
            activeBan.EndAtUtc
        });
    }

    var deviceBound = await store.IsDeviceBoundToAccountAsync(
        request.DeviceId,
        request.AccountId,
        request.SteamId,
        cancellationToken);
    if (!deviceBound)
    {
        await TryWriteSecurityEventAsync(
            store,
            eventType: "device_binding_mismatch",
            severity: "high",
            source: "attestation.match_start",
            accountId: request.AccountId,
            matchSessionId: request.MatchSessionId,
            ipAddress: null,
            details: new { request.DeviceId, reason = "unknown_or_mismatched_device" },
            cancellationToken);
        return Results.BadRequest(new { error = "unknown_or_mismatched_device" });
    }

    if (!request.PlatformSignals.SecureBoot || !request.PlatformSignals.Tpm20)
    {
        await TryWriteSecurityEventAsync(
            store,
            eventType: "platform_tier_a_failed",
            severity: "high",
            source: "attestation.match_start",
            accountId: request.AccountId,
            matchSessionId: request.MatchSessionId,
            ipAddress: null,
            details: request.PlatformSignals,
            cancellationToken);
        return Results.BadRequest(new { error = "tier_a_requirements_not_met" });
    }

    var queueType = await store.GetQueueTypeForMatchAsync(request.MatchSessionId, cancellationToken) ?? "standard";
    if (string.Equals(queueType, "high_trust", StringComparison.OrdinalIgnoreCase))
    {
        if (policyOptions.Value.RequiredTierB.Iommu && !request.PlatformSignals.Iommu)
        {
            await TryWriteSecurityEventAsync(
                store,
                eventType: "platform_tier_b_failed",
                severity: "medium",
                source: "attestation.match_start",
                accountId: request.AccountId,
                matchSessionId: request.MatchSessionId,
                ipAddress: null,
                details: new { reason = "iommu_required" },
                cancellationToken);
            return Results.BadRequest(new { error = "tier_b_iommu_required_for_high_trust" });
        }

        if (policyOptions.Value.RequiredTierB.Vbs && !request.PlatformSignals.Vbs)
        {
            await TryWriteSecurityEventAsync(
                store,
                eventType: "platform_tier_b_failed",
                severity: "medium",
                source: "attestation.match_start",
                accountId: request.AccountId,
                matchSessionId: request.MatchSessionId,
                ipAddress: null,
                details: new { reason = "vbs_required" },
                cancellationToken);
            return Results.BadRequest(new { error = "tier_b_vbs_required_for_high_trust" });
        }
    }

    var requireAntiTamperOnMatchStart = policyOptions.Value.RequireAntiTamperOnMatchStart;
    if (!request.IntegritySignals.AcServiceHealthy ||
        !request.IntegritySignals.ModulePolicyOk ||
        !request.IntegritySignals.DriverLoaded ||
        (requireAntiTamperOnMatchStart && !request.IntegritySignals.AntiTamperOk))
    {
        await TryWriteSecurityEventAsync(
            store,
            eventType: "integrity_signal_failed",
            severity: "high",
            source: "attestation.match_start",
            accountId: request.AccountId,
            matchSessionId: request.MatchSessionId,
            ipAddress: null,
            details: request.IntegritySignals,
            cancellationToken);
        return Results.BadRequest(new { error = "integrity_signal_failed" });
    }

    if (!IsAllowedPolicyHash(policyOptions.Value.RequiredPolicyHashes, request.IntegritySignals.PolicyHash))
    {
        await TryWriteSecurityEventAsync(
            store,
            eventType: "policy_hash_not_allowed",
            severity: "high",
            source: "attestation.match_start",
            accountId: request.AccountId,
            matchSessionId: request.MatchSessionId,
            ipAddress: null,
            details: new { request.IntegritySignals.PolicyHash },
            cancellationToken);
        return Results.BadRequest(new { error = "policy_hash_not_allowed" });
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

    var deviceBound = await store.IsDeviceBoundToAccountAsync(
        request.DeviceId,
        request.AccountId,
        steamId,
        cancellationToken);
    if (!deviceBound)
    {
        var mismatchAt = DateTimeOffset.UtcNow;
        await store.AddHeartbeatAsync(request, steamId, "unhealthy", cancellationToken);
        await store.UpsertPlayerHealthAsync(
            request.MatchSessionId,
            request.AccountId,
            steamId,
            "unhealthy",
            mismatchAt,
            "kick",
            cancellationToken);

        await TryWriteSecurityEventAsync(
            store,
            eventType: "device_binding_mismatch",
            severity: "high",
            source: "attestation.heartbeat",
            accountId: request.AccountId,
            matchSessionId: request.MatchSessionId,
            ipAddress: null,
            details: new { request.DeviceId, request.Sequence },
            cancellationToken);

        return Results.Ok(new HeartbeatResponse(
            "unhealthy",
            policyOptions.Value.HeartbeatIntervalSec,
            "kick"));
    }

    var activeBan = await store.GetActiveBanForAccountAsync(request.AccountId, cancellationToken);
    if (activeBan is not null)
    {
        var bannedAt = DateTimeOffset.UtcNow;
        await store.AddHeartbeatAsync(request, steamId, "banned", cancellationToken);
        await store.UpsertPlayerHealthAsync(
            request.MatchSessionId,
            request.AccountId,
            steamId,
            "banned",
            bannedAt,
            "kick",
            cancellationToken);

        await TryWriteSecurityEventAsync(
            store,
            eventType: "heartbeat_banned_account",
            severity: "high",
            source: "attestation.heartbeat",
            accountId: request.AccountId,
            matchSessionId: request.MatchSessionId,
            ipAddress: null,
            details: new { activeBan.BanId, request.Sequence },
            cancellationToken);

        return Results.Ok(new HeartbeatResponse(
            "banned",
            policyOptions.Value.HeartbeatIntervalSec,
            "kick"));
    }

    var healthy =
        request.PlatformSignals.SecureBoot &&
        request.PlatformSignals.Tpm20 &&
        request.IntegritySignals.AcServiceHealthy &&
        request.IntegritySignals.AntiTamperOk &&
        request.IntegritySignals.ModulePolicyOk &&
        request.IntegritySignals.DriverLoaded &&
        IsAllowedPolicyHash(policyOptions.Value.RequiredPolicyHashes, request.IntegritySignals.PolicyHash);

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

    if (!healthy)
    {
        await TryWriteSecurityEventAsync(
            store,
            eventType: "heartbeat_unhealthy",
            severity: "medium",
            source: "attestation.heartbeat",
            accountId: request.AccountId,
            matchSessionId: request.MatchSessionId,
            ipAddress: null,
            details: request.IntegritySignals,
            cancellationToken);
    }

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

    var activeBan = await store.GetActiveBanForAccountAsync(request.AccountId, cancellationToken);
    if (activeBan is not null)
    {
        await TryWriteSecurityEventAsync(
            store,
            eventType: "join_denied_banned_account",
            severity: "high",
            source: "attestation.validate_join",
            accountId: request.AccountId,
            matchSessionId: request.MatchSessionId,
            ipAddress: ReadRemoteAddress(context),
            details: new { request.ServerId, activeBan.BanId, activeBan.Scope },
            cancellationToken);
        return Results.Ok(new ValidateJoinResponse(
            Allow: false,
            Reason: "account_banned",
            HeartbeatStatus: "unhealthy",
            TrustTier: $"banned_{activeBan.Scope.ToLowerInvariant()}"));
    }

    if (!tokenService.TryValidate(request.JoinToken, out var payload, out var reason) || payload is null)
    {
        await TryWriteSecurityEventAsync(
            store,
            eventType: "join_denied_invalid_token",
            severity: "high",
            source: "attestation.validate_join",
            accountId: request.AccountId,
            matchSessionId: request.MatchSessionId,
            ipAddress: ReadRemoteAddress(context),
            details: new { request.ServerId, reason = reason ?? "invalid_token" },
            cancellationToken);
        return Results.Ok(new ValidateJoinResponse(false, reason ?? "invalid_token", "unknown", "unknown"));
    }

    if (!string.Equals(payload.MatchSessionId, request.MatchSessionId, StringComparison.Ordinal) ||
        !string.Equals(payload.AccountId, request.AccountId, StringComparison.Ordinal) ||
        !string.Equals(payload.SteamId, request.SteamId, StringComparison.Ordinal) ||
        !string.Equals(payload.ServerId, request.ServerId, StringComparison.Ordinal))
    {
        await TryWriteSecurityEventAsync(
            store,
            eventType: "join_denied_claim_mismatch",
            severity: "high",
            source: "attestation.validate_join",
            accountId: request.AccountId,
            matchSessionId: request.MatchSessionId,
            ipAddress: ReadRemoteAddress(context),
            details: new
            {
                expected = new { request.AccountId, request.SteamId, request.ServerId, request.MatchSessionId },
                token = new { payload.AccountId, payload.SteamId, payload.ServerId, payload.MatchSessionId }
            },
            cancellationToken);
        return Results.Ok(new ValidateJoinResponse(false, "token_claim_mismatch", "unknown", "unknown"));
    }

    var tokenRecord = await store.GetJoinTokenAsync(payload.Jti, cancellationToken);
    if (tokenRecord is null)
    {
        await TryWriteSecurityEventAsync(
            store,
            eventType: "join_denied_token_not_issued",
            severity: "high",
            source: "attestation.validate_join",
            accountId: request.AccountId,
            matchSessionId: request.MatchSessionId,
            ipAddress: ReadRemoteAddress(context),
            details: new { payload.Jti },
            cancellationToken);
        return Results.Ok(new ValidateJoinResponse(false, "token_not_issued", "unknown", "unknown"));
    }

    if (tokenRecord.UsedAtUtc is not null)
    {
        await TryWriteSecurityEventAsync(
            store,
            eventType: "join_denied_token_replayed",
            severity: "high",
            source: "attestation.validate_join",
            accountId: request.AccountId,
            matchSessionId: request.MatchSessionId,
            ipAddress: ReadRemoteAddress(context),
            details: new { payload.Jti, tokenRecord.UsedAtUtc },
            cancellationToken);
        return Results.Ok(new ValidateJoinResponse(false, "token_replayed", "unknown", "unknown"));
    }

    var latestHeartbeat = await store.GetLatestHeartbeatAsync(request.MatchSessionId, request.AccountId, cancellationToken);
    if (latestHeartbeat is null)
    {
        await TryWriteSecurityEventAsync(
            store,
            eventType: "join_denied_missing_heartbeat",
            severity: "medium",
            source: "attestation.validate_join",
            accountId: request.AccountId,
            matchSessionId: request.MatchSessionId,
            ipAddress: ReadRemoteAddress(context),
            details: new { payload.Jti },
            cancellationToken);
        return Results.Ok(new ValidateJoinResponse(false, "missing_heartbeat", "unhealthy", "unknown"));
    }

    var staleCutoff = DateTimeOffset.UtcNow.AddSeconds(-policyOptions.Value.GraceWindowSec);
    if (latestHeartbeat.ReceivedAtUtc < staleCutoff)
    {
        await TryWriteSecurityEventAsync(
            store,
            eventType: "join_denied_stale_heartbeat",
            severity: "medium",
            source: "attestation.validate_join",
            accountId: request.AccountId,
            matchSessionId: request.MatchSessionId,
            ipAddress: ReadRemoteAddress(context),
            details: new { latestHeartbeat.ReceivedAtUtc, staleCutoff },
            cancellationToken);
        return Results.Ok(new ValidateJoinResponse(false, "stale_heartbeat", "unhealthy", "unknown"));
    }

    if (!string.Equals(latestHeartbeat.Status, "healthy", StringComparison.OrdinalIgnoreCase))
    {
        await TryWriteSecurityEventAsync(
            store,
            eventType: "join_denied_unhealthy_heartbeat",
            severity: "medium",
            source: "attestation.validate_join",
            accountId: request.AccountId,
            matchSessionId: request.MatchSessionId,
            ipAddress: ReadRemoteAddress(context),
            details: new { latestHeartbeat.Status },
            cancellationToken);
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
    IOptions<AcPolicyOptions> policyOptions,
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
    var actionDedupSec = Math.Max(1, policyOptions.Value.Detection.ActionCooldownSec);
    await PersistDetectionResultAsync(
        result,
        envelope.MatchSessionId,
        actionDedupSec,
        store,
        evidenceService,
        cancellationToken);
    return Results.Ok(new { ingested = envelope.Items.Count, scores = result.ScoreUpdates.Count, actions = result.EnforcementActions.Count });
});

app.MapPost("/v1/telemetry/shots", async (
    TelemetryEnvelope<ShotEvent> envelope,
    HttpContext context,
    ISqliteStore store,
    IDetectionEngine detectionEngine,
    IEvidenceService evidenceService,
    IOptions<AcPolicyOptions> policyOptions,
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
    var actionDedupSec = Math.Max(1, policyOptions.Value.Detection.ActionCooldownSec);
    await PersistDetectionResultAsync(
        result,
        envelope.MatchSessionId,
        actionDedupSec,
        store,
        evidenceService,
        cancellationToken);
    return Results.Ok(new { ingested = envelope.Items.Count, scores = result.ScoreUpdates.Count, actions = result.EnforcementActions.Count });
});

app.MapPost("/v1/telemetry/los", async (
    TelemetryEnvelope<LosSample> envelope,
    HttpContext context,
    ISqliteStore store,
    IDetectionEngine detectionEngine,
    IEvidenceService evidenceService,
    IOptions<AcPolicyOptions> policyOptions,
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
    var actionDedupSec = Math.Max(1, policyOptions.Value.Detection.ActionCooldownSec);
    await PersistDetectionResultAsync(
        result,
        envelope.MatchSessionId,
        actionDedupSec,
        store,
        evidenceService,
        cancellationToken);
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
    int? limit,
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

    var actions = await store.GetEnforcementActionsAsync(matchSessionId, limit ?? 200, cancellationToken);
    return Results.Ok(actions);
});

app.MapGet("/v1/enforcement/actions/{matchSessionId}/pending", async (
    string matchSessionId,
    string? accountId,
    int? limit,
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

    var actions = await store.GetPendingEnforcementActionsAsync(matchSessionId, accountId, limit ?? 200, cancellationToken);
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

app.MapGet("/v1/moderation/bans", async (
    string? accountId,
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

    var bans = await store.ListBansAsync(accountId, status, cancellationToken);
    return Results.Ok(bans);
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

app.MapPost("/v1/moderation/bans/status", async (
    UpdateBanStatusRequest request,
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

    var updated = await store.UpdateBanStatusAsync(request, cancellationToken);
    return updated is null
        ? Results.NotFound(new { error = "ban_not_found" })
        : Results.Ok(updated);
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
    int actionDedupSec,
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
        var alreadyQueued = await store.HasRecentEnforcementActionAsync(
            action.MatchSessionId,
            action.AccountId,
            action.ActionType,
            action.ReasonCode,
            actionDedupSec,
            cancellationToken);
        if (alreadyQueued)
        {
            continue;
        }

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
static string HashAccessToken(string token) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

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

static RateLimitPartition<string> CreatePartition(string key, FixedWindowBucketOptions options)
{
    return RateLimitPartition.GetFixedWindowLimiter(
        key,
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.PermitLimit,
            Window = TimeSpan.FromSeconds(options.WindowSeconds),
            QueueLimit = options.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });
}

static string ResolveRateLimitPolicy(string path)
{
    if (path.StartsWith("/v1/auth/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/v1/queue/", StringComparison.OrdinalIgnoreCase))
    {
        return "public_auth";
    }

    if (path.StartsWith("/v1/attestation/enroll", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/v1/attestation/match-start", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/v1/attestation/heartbeat", StringComparison.OrdinalIgnoreCase))
    {
        return "public_client";
    }

    if (path.StartsWith("/v1/attestation/validate-join", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/v1/attestation/match-health", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/v1/telemetry/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/v1/enforcement/actions", StringComparison.OrdinalIgnoreCase))
    {
        return "server_api";
    }

    if (path.StartsWith("/v1/evidence", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/v1/metrics/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/v1/ops/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/v1/review/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/v1/moderation/", StringComparison.OrdinalIgnoreCase))
    {
        return "internal_api";
    }

    return "system";
}

static string ReadHeaderOrFallback(HttpContext context, string headerName)
{
    if (context.Request.Headers.TryGetValue(headerName, out var value) &&
        !string.IsNullOrWhiteSpace(value))
    {
        return value.ToString();
    }

    return ReadRemoteAddress(context);
}

static string ReadRemoteAddress(HttpContext context)
{
    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

static string? TryReadBearerToken(HttpContext context)
{
    if (!context.Request.Headers.TryGetValue("Authorization", out var headerValues))
    {
        return null;
    }

    var value = headerValues.ToString();
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    const string prefix = "Bearer ";
    if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    return value[prefix.Length..].Trim();
}

static async Task TryWriteSecurityEventAsync(
    ISqliteStore store,
    string eventType,
    string severity,
    string source,
    string? accountId,
    string? matchSessionId,
    string? ipAddress,
    object? details,
    CancellationToken cancellationToken)
{
    var normalizedEventType = string.IsNullOrWhiteSpace(eventType)
        ? "unknown_event"
        : eventType.Trim().ToLowerInvariant();
    var normalizedSeverity = string.IsNullOrWhiteSpace(severity)
        ? "low"
        : severity.Trim().ToLowerInvariant();
    var normalizedSource = string.IsNullOrWhiteSpace(source)
        ? "unknown_source"
        : source.Trim().ToLowerInvariant();
    var detailsJson = details is null ? "{}" : JsonSerializer.Serialize(details);

    try
    {
        await store.AddSecurityEventAsync(
            normalizedEventType,
            normalizedSeverity,
            normalizedSource,
            string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim(),
            string.IsNullOrWhiteSpace(matchSessionId) ? null : matchSessionId.Trim(),
            string.IsNullOrWhiteSpace(ipAddress) ? null : ipAddress.Trim(),
            detailsJson,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }
    catch
    {
        // Security-event persistence must never break primary request flow.
    }
}

static bool IsAllowedPolicyHash(IReadOnlyList<string>? requiredPolicyHashes, string providedPolicyHash)
{
    if (requiredPolicyHashes is null || requiredPolicyHashes.Count == 0)
    {
        return true;
    }

    if (string.IsNullOrWhiteSpace(providedPolicyHash))
    {
        return false;
    }

    var normalizedProvided = providedPolicyHash.Trim();
    foreach (var allowedHash in requiredPolicyHashes)
    {
        if (string.IsNullOrWhiteSpace(allowedHash))
        {
            continue;
        }

        if (string.Equals(allowedHash.Trim(), normalizedProvided, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return false;
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

namespace Shared.Contracts;

public sealed record LoginRequest(string Username, string Password);

public sealed record LoginResponse(string AccountId, string AccessToken, string SteamId, bool SteamLinked);

public sealed record QueueRequest(string AccountId, string SteamId, string QueueType);

public sealed record QueueResponse(
    string MatchSessionId,
    string ServerId,
    string Region,
    string QueueType,
    DateTimeOffset EstimatedStartUtc);

public sealed record EnrollRequest(
    string AccountId,
    string SteamId,
    string DevicePublicKeyPem,
    string LauncherVersion,
    string AcVersion);

public sealed record EnrollResponse(
    string DeviceId,
    string DeviceCertificate,
    string PolicyVersion,
    string RequirementsTier);

public sealed record PlatformSignals(bool SecureBoot, bool Tpm20, bool Iommu, bool Vbs);

public sealed record IntegritySignals(
    bool AcServiceHealthy,
    bool DriverLoaded,
    bool ModulePolicyOk,
    bool AntiTamperOk,
    string PolicyHash);

public sealed record MatchStartRequest(
    string MatchSessionId,
    string AccountId,
    string SteamId,
    string DeviceId,
    string Nonce,
    PlatformSignals PlatformSignals,
    IntegritySignals IntegritySignals);

public sealed record MatchStartResponse(
    string JoinToken,
    int JoinTokenTtlSec,
    int HeartbeatIntervalSec,
    int GraceWindowSec,
    string QueueTier);

public sealed record HeartbeatRequest(
    string MatchSessionId,
    string AccountId,
    string DeviceId,
    long Sequence,
    PlatformSignals PlatformSignals,
    IntegritySignals IntegritySignals,
    int HeartbeatLatencyMs);

public sealed record HeartbeatResponse(string Status, int NextIntervalSec, string ServerActionHint);

public sealed record ValidateJoinRequest(
    string ServerId,
    string SteamId,
    string AccountId,
    string MatchSessionId,
    string JoinToken);

public sealed record ValidateJoinResponse(bool Allow, string Reason, string HeartbeatStatus, string TrustTier);

public sealed record MatchHealthQuery(string MatchSessionId);

public sealed record PlayerHealthState(
    string AccountId,
    string SteamId,
    string Status,
    DateTimeOffset LastHeartbeatUtc,
    string RecommendedAction);

public sealed record MatchHealthResponse(
    string MatchSessionId,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<PlayerHealthState> Players);

public sealed record TickPlayerState(
    string MatchSessionId,
    long TickId,
    DateTimeOffset TickUtc,
    string AccountId,
    string SteamId,
    string Team,
    float PosX,
    float PosY,
    float PosZ,
    float VelX,
    float VelY,
    float VelZ,
    float Yaw,
    float Pitch,
    string Stance,
    string WeaponId,
    int AmmoClip,
    bool IsReloading,
    int PingMs,
    float LossPct,
    float ChokePct);

public sealed record ShotEvent(
    string MatchSessionId,
    long TickId,
    DateTimeOffset TickUtc,
    string ShooterAccountId,
    string ShooterSteamId,
    string WeaponId,
    int RecoilIndex,
    float Yaw,
    float Pitch,
    bool HitPlayer,
    string? HitAccountId,
    string? HitSteamId);

public sealed record LosSample(
    string MatchSessionId,
    long TickId,
    DateTimeOffset TickUtc,
    string ObserverAccountId,
    string TargetAccountId,
    bool LineOfSight,
    bool AudibleProxy,
    float DistanceMeters);

public sealed record TelemetryEnvelope<T>(
    string MatchSessionId,
    string Source,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<T> Items);

public sealed record SuspicionScoreUpdate(
    string MatchSessionId,
    string AccountId,
    string Channel,
    double Score,
    double Confidence,
    int SampleSize,
    DateTimeOffset UpdatedAtUtc);

public sealed record EnforcementAction(
    string ActionId,
    string MatchSessionId,
    string AccountId,
    string ActionType,
    string ReasonCode,
    int DurationSeconds,
    DateTimeOffset CreatedAtUtc);

public sealed record EnforcementActionAckRequest(
    string ActionId,
    string MatchSessionId,
    string AccountId,
    string ExecutorId,
    string Result,
    string Notes,
    DateTimeOffset AckedAtUtc);

public sealed record EnforcementActionAckResponse(
    bool Accepted,
    string Reason,
    DateTimeOffset ReceivedAtUtc);

public sealed record EvidencePackSummary(
    string EvidenceId,
    string MatchSessionId,
    string AccountId,
    string TriggerType,
    string? ActionId,
    string StoragePath,
    string ContentSha256,
    DateTimeOffset CreatedAtUtc,
    string ReviewStatus);

public sealed record CreateReviewCaseRequest(
    string EvidenceId,
    string MatchSessionId,
    string AccountId,
    string ReasonCode,
    string Priority,
    string RequestedBy);

public sealed record ReviewCaseSummary(
    string CaseId,
    string EvidenceId,
    string MatchSessionId,
    string AccountId,
    string ReasonCode,
    string Priority,
    string Status,
    string? AssignedReviewer,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record UpdateReviewCaseRequest(
    string CaseId,
    string Status,
    string ReviewerId,
    string Notes);

public sealed record BanRecord(
    string BanId,
    string AccountId,
    string Scope,
    string Status,
    DateTimeOffset StartAtUtc,
    DateTimeOffset? EndAtUtc,
    string Reason,
    string? EvidenceId,
    DateTimeOffset CreatedAtUtc);

public sealed record CreateBanRequest(
    string AccountId,
    string Scope,
    DateTimeOffset StartAtUtc,
    DateTimeOffset? EndAtUtc,
    string Reason,
    string? EvidenceId,
    string CreatedBy);

public sealed record AppealRecord(
    string AppealId,
    string BanId,
    string AccountId,
    string Status,
    DateTimeOffset SubmittedAtUtc,
    DateTimeOffset? DecisionAtUtc,
    string? ReviewerId,
    string? DecisionNotes);

public sealed record CreateAppealRequest(
    string BanId,
    string AccountId,
    string Notes);

public sealed record ResolveAppealRequest(
    string AppealId,
    string ReviewerId,
    string Status,
    string DecisionNotes);

public sealed record QueueSessionState(
    string AccountId,
    string SteamId,
    string MatchSessionId,
    string ServerId,
    string QueueType,
    DateTimeOffset CreatedAtUtc);

public sealed record JoinTokenPayload(
    string Jti,
    string AccountId,
    string SteamId,
    string MatchSessionId,
    string ServerId,
    string DeviceId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

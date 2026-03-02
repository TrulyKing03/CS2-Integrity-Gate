using Shared.Contracts;

namespace Cs2.Plugin.CounterStrikeSharp;

public sealed record PlayerConnectionAttempt(
    string MatchSessionId,
    string ServerId,
    string AccountId,
    string SteamId,
    string JoinToken);

public sealed record PlayerSessionIdentity(
    string MatchSessionId,
    string AccountId,
    string SteamId);

public sealed record TickSample(
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
    float ChokePct)
{
    public TickPlayerState ToContract() =>
        new(
            MatchSessionId,
            TickId,
            TickUtc,
            AccountId,
            SteamId,
            Team,
            PosX,
            PosY,
            PosZ,
            VelX,
            VelY,
            VelZ,
            Yaw,
            Pitch,
            Stance,
            WeaponId,
            AmmoClip,
            IsReloading,
            PingMs,
            LossPct,
            ChokePct);
}

public sealed record ShotSample(
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
    string? HitSteamId)
{
    public ShotEvent ToContract() =>
        new(
            MatchSessionId,
            TickId,
            TickUtc,
            ShooterAccountId,
            ShooterSteamId,
            WeaponId,
            RecoilIndex,
            Yaw,
            Pitch,
            HitPlayer,
            HitAccountId,
            HitSteamId);
}

public sealed record VisibilitySample(
    string MatchSessionId,
    long TickId,
    DateTimeOffset TickUtc,
    string ObserverAccountId,
    string TargetAccountId,
    bool LineOfSight,
    bool AudibleProxy,
    float DistanceMeters)
{
    public LosSample ToContract() =>
        new(
            MatchSessionId,
            TickId,
            TickUtc,
            ObserverAccountId,
            TargetAccountId,
            LineOfSight,
            AudibleProxy,
            DistanceMeters);
}

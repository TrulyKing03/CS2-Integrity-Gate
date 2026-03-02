using System.Collections.Concurrent;
using Shared.Contracts;

namespace ControlPlane.Api.Services;

public interface IDetectionEngine
{
    DetectionResult ProcessTicks(TelemetryEnvelope<TickPlayerState> envelope);
    DetectionResult ProcessShots(TelemetryEnvelope<ShotEvent> envelope);
    DetectionResult ProcessLosSamples(TelemetryEnvelope<LosSample> envelope);
}

public sealed record DetectionResult(
    IReadOnlyList<SuspicionScoreUpdate> ScoreUpdates,
    IReadOnlyList<EnforcementAction> EnforcementActions);

internal sealed class DetectionEngine : IDetectionEngine
{
    private const int MinAimSamples = 30;
    private const int MinTriggerSamples = 20;
    private const int MinInfoSamples = 25;
    private const int MinMovementSamples = 80;

    private readonly ConcurrentDictionary<string, PlayerDetectionState> _playerStates = new();
    private readonly ConcurrentDictionary<string, LosObservation> _losState = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _actionCooldown = new();

    public DetectionResult ProcessTicks(TelemetryEnvelope<TickPlayerState> envelope)
    {
        var updates = new List<SuspicionScoreUpdate>();
        var actions = new List<EnforcementAction>();
        foreach (var tick in envelope.Items)
        {
            var playerState = GetOrCreatePlayerState(tick.MatchSessionId, tick.AccountId);
            var speed = MathF.Sqrt(
                tick.VelX * tick.VelX +
                tick.VelY * tick.VelY +
                tick.VelZ * tick.VelZ);
            playerState.MovementSamples++;
            if (speed > 400f)
            {
                playerState.MovementViolations++;
                playerState.RuleViolations++;
            }

            playerState.LastTickState = tick;
            playerState.LastUpdatedUtc = DateTimeOffset.UtcNow;

            if (playerState.MovementSamples >= MinMovementSamples)
            {
                var ratio = (double)playerState.MovementViolations / playerState.MovementSamples;
                var score = ClampTo100(ratio * 220.0);
                updates.Add(new SuspicionScoreUpdate(
                    tick.MatchSessionId,
                    tick.AccountId,
                    "movement",
                    score,
                    Confidence(playerState.MovementSamples, MinMovementSamples),
                    playerState.MovementSamples,
                    DateTimeOffset.UtcNow));
            }

            if (playerState.RuleViolations >= 3)
            {
                var score = ClampTo100(playerState.RuleViolations * 20);
                updates.Add(new SuspicionScoreUpdate(
                    tick.MatchSessionId,
                    tick.AccountId,
                    "rules",
                    score,
                    0.95,
                    playerState.RuleViolations,
                    DateTimeOffset.UtcNow));

                var action = TryCreateAction(
                    tick.MatchSessionId,
                    tick.AccountId,
                    "kick",
                    "rules_impossible_state");
                if (action is not null)
                {
                    actions.Add(action);
                }
            }
        }

        return new DetectionResult(updates, actions);
    }

    public DetectionResult ProcessShots(TelemetryEnvelope<ShotEvent> envelope)
    {
        var updates = new List<SuspicionScoreUpdate>();
        var actions = new List<EnforcementAction>();

        foreach (var shot in envelope.Items)
        {
            var playerState = GetOrCreatePlayerState(shot.MatchSessionId, shot.ShooterAccountId);
            playerState.Shots++;
            if (shot.HitPlayer)
            {
                playerState.Hits++;
            }

            if (playerState.LastShotTickByWeapon.TryGetValue(shot.WeaponId, out var previousTick))
            {
                var delta = shot.TickId - previousTick;
                var minTicks = MinShotIntervalTicks(shot.WeaponId);
                if (delta > 0 && delta < minTicks)
                {
                    playerState.RuleViolations++;
                }
            }

            playerState.LastShotTickByWeapon[shot.WeaponId] = shot.TickId;

            if (playerState.LastTickState?.IsReloading is true)
            {
                playerState.RuleViolations++;
            }

            if (!string.IsNullOrWhiteSpace(shot.HitAccountId))
            {
                var losKey = BuildLosKey(shot.MatchSessionId, shot.ShooterAccountId, shot.HitAccountId);
                if (_losState.TryGetValue(losKey, out var los))
                {
                    if (!los.LineOfSight && !los.AudibleProxy && shot.TickId - los.TickId <= 8)
                    {
                        playerState.InfoSamples++;
                        playerState.InfoViolations++;
                    }
                    else
                    {
                        playerState.InfoSamples++;
                    }

                    if (los.LineOfSight)
                    {
                        playerState.TriggerSamples++;
                        var delta = shot.TickId - los.TickId;
                        if (delta <= 1)
                        {
                            playerState.TriggerNearZero++;
                        }
                    }
                }
            }

            if (playerState.Shots >= MinAimSamples)
            {
                var hitRatio = (double)playerState.Hits / Math.Max(1, playerState.Shots);
                var aimScore = ClampTo100(Math.Max(0, (hitRatio - 0.45) * 180.0));
                updates.Add(new SuspicionScoreUpdate(
                    shot.MatchSessionId,
                    shot.ShooterAccountId,
                    "aim",
                    aimScore,
                    Confidence(playerState.Shots, MinAimSamples),
                    playerState.Shots,
                    DateTimeOffset.UtcNow));
            }

            if (playerState.TriggerSamples >= MinTriggerSamples)
            {
                var ratio = (double)playerState.TriggerNearZero / Math.Max(1, playerState.TriggerSamples);
                var triggerScore = ClampTo100(ratio * 170.0);
                updates.Add(new SuspicionScoreUpdate(
                    shot.MatchSessionId,
                    shot.ShooterAccountId,
                    "trigger",
                    triggerScore,
                    Confidence(playerState.TriggerSamples, MinTriggerSamples),
                    playerState.TriggerSamples,
                    DateTimeOffset.UtcNow));
            }

            if (playerState.InfoSamples >= MinInfoSamples)
            {
                var ratio = (double)playerState.InfoViolations / Math.Max(1, playerState.InfoSamples);
                var infoScore = ClampTo100(ratio * 160.0);
                updates.Add(new SuspicionScoreUpdate(
                    shot.MatchSessionId,
                    shot.ShooterAccountId,
                    "info",
                    infoScore,
                    Confidence(playerState.InfoSamples, MinInfoSamples),
                    playerState.InfoSamples,
                    DateTimeOffset.UtcNow));
            }

            if (playerState.RuleViolations >= 4)
            {
                var ruleScore = ClampTo100(playerState.RuleViolations * 20.0);
                updates.Add(new SuspicionScoreUpdate(
                    shot.MatchSessionId,
                    shot.ShooterAccountId,
                    "rules",
                    ruleScore,
                    0.98,
                    playerState.RuleViolations,
                    DateTimeOffset.UtcNow));

                var action = TryCreateAction(
                    shot.MatchSessionId,
                    shot.ShooterAccountId,
                    "kick",
                    "rules_fire_cadence");
                if (action is not null)
                {
                    actions.Add(action);
                }
            }
        }

        return new DetectionResult(updates, actions);
    }

    public DetectionResult ProcessLosSamples(TelemetryEnvelope<LosSample> envelope)
    {
        foreach (var sample in envelope.Items)
        {
            _losState[BuildLosKey(sample.MatchSessionId, sample.ObserverAccountId, sample.TargetAccountId)] =
                new LosObservation(sample.TickId, sample.LineOfSight, sample.AudibleProxy);
        }

        return new DetectionResult(Array.Empty<SuspicionScoreUpdate>(), Array.Empty<EnforcementAction>());
    }

    private PlayerDetectionState GetOrCreatePlayerState(string matchSessionId, string accountId)
    {
        return _playerStates.GetOrAdd(
            $"{matchSessionId}|{accountId}",
            _ => new PlayerDetectionState());
    }

    private static string BuildLosKey(string matchSessionId, string observer, string target) => $"{matchSessionId}|{observer}|{target}";

    private static int MinShotIntervalTicks(string weaponId)
    {
        return weaponId.ToLowerInvariant() switch
        {
            "ak47" => 5,
            "m4a1" => 5,
            "m4a1_silencer" => 5,
            "awp" => 35,
            "deagle" => 12,
            _ => 4
        };
    }

    private EnforcementAction? TryCreateAction(
        string matchSessionId,
        string accountId,
        string actionType,
        string reasonCode)
    {
        var key = $"{matchSessionId}|{accountId}|{reasonCode}";
        var now = DateTimeOffset.UtcNow;
        if (_actionCooldown.TryGetValue(key, out var previous) && now - previous < TimeSpan.FromSeconds(30))
        {
            return null;
        }

        _actionCooldown[key] = now;
        return new EnforcementAction(
            Guid.NewGuid().ToString("N"),
            matchSessionId,
            accountId,
            actionType,
            reasonCode,
            0,
            now);
    }

    private static double ClampTo100(double value) => Math.Max(0, Math.Min(100, value));

    private static double Confidence(int samples, int minSamples)
    {
        if (minSamples <= 0)
        {
            return 1.0;
        }

        return Math.Max(0.15, Math.Min(1.0, (double)samples / minSamples));
    }

    private sealed class PlayerDetectionState
    {
        public int MovementSamples { get; set; }
        public int MovementViolations { get; set; }
        public int RuleViolations { get; set; }
        public int Shots { get; set; }
        public int Hits { get; set; }
        public int TriggerSamples { get; set; }
        public int TriggerNearZero { get; set; }
        public int InfoSamples { get; set; }
        public int InfoViolations { get; set; }
        public TickPlayerState? LastTickState { get; set; }
        public DateTimeOffset LastUpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public Dictionary<string, long> LastShotTickByWeapon { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record LosObservation(long TickId, bool LineOfSight, bool AudibleProxy);
}

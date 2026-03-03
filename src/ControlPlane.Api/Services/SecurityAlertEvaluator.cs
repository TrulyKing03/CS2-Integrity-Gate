using System.Text.Json;
using ControlPlane.Api.Options;
using ControlPlane.Api.Persistence;
using Microsoft.Extensions.Options;

namespace ControlPlane.Api.Services;

public interface ISecurityAlertEvaluator
{
    Task<SecurityAlertEvaluationResult> EvaluateAsync(
        string source,
        bool force,
        CancellationToken cancellationToken);
}

public sealed record SecurityAlertEvaluationResult(
    DateTimeOffset EvaluatedAtUtc,
    DateTimeOffset WindowStartUtc,
    int HighCount,
    int MediumCount,
    bool ThresholdExceeded,
    bool AlertTriggered,
    bool SuppressedByCooldown,
    bool Disabled,
    string AlertLevel,
    string Reason,
    DateTimeOffset? LastAlertAtUtc);

public sealed class SecurityAlertEvaluator(
    ISqliteStore store,
    IOptions<SecurityAlertOptions> optionsAccessor,
    SecurityAlertState state,
    ILogger<SecurityAlertEvaluator> logger) : ISecurityAlertEvaluator
{
    private readonly SecurityAlertOptions _options = optionsAccessor.Value;

    public async Task<SecurityAlertEvaluationResult> EvaluateAsync(
        string source,
        bool force,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var windowStart = now.AddMinutes(-Math.Max(1, _options.WindowMinutes));

        try
        {
            if (!_options.Enabled && !force)
            {
                state.SetEvaluation(now, 0, 0);
                return new SecurityAlertEvaluationResult(
                    EvaluatedAtUtc: now,
                    WindowStartUtc: windowStart,
                    HighCount: 0,
                    MediumCount: 0,
                    ThresholdExceeded: false,
                    AlertTriggered: false,
                    SuppressedByCooldown: false,
                    Disabled: true,
                    AlertLevel: "none",
                    Reason: "alerts_disabled",
                    LastAlertAtUtc: state.LastAlertAtUtc);
            }

            var summary = await store.GetSecurityEventSummaryAsync(windowStart, cancellationToken);
            var relevant = summary.Where(row =>
                !string.Equals(row.EventType, "security_alert_triggered", StringComparison.OrdinalIgnoreCase));

            var highCount = relevant
                .Where(row => string.Equals(row.Severity, "high", StringComparison.OrdinalIgnoreCase))
                .Sum(row => row.Count);
            var mediumCount = relevant
                .Where(row => string.Equals(row.Severity, "medium", StringComparison.OrdinalIgnoreCase))
                .Sum(row => row.Count);
            state.SetEvaluation(now, highCount, mediumCount);

            var thresholdExceeded = false;
            var level = "none";
            var reason = "within_thresholds";
            if (highCount >= Math.Max(1, _options.HighThreshold))
            {
                thresholdExceeded = true;
                level = "high";
                reason = $"high_count={highCount} threshold={_options.HighThreshold}";
            }
            else if (mediumCount >= Math.Max(1, _options.MediumThreshold))
            {
                thresholdExceeded = true;
                level = "medium";
                reason = $"medium_count={mediumCount} threshold={_options.MediumThreshold}";
            }

            if (!thresholdExceeded)
            {
                return new SecurityAlertEvaluationResult(
                    EvaluatedAtUtc: now,
                    WindowStartUtc: windowStart,
                    HighCount: highCount,
                    MediumCount: mediumCount,
                    ThresholdExceeded: false,
                    AlertTriggered: false,
                    SuppressedByCooldown: false,
                    Disabled: !_options.Enabled,
                    AlertLevel: "none",
                    Reason: reason,
                    LastAlertAtUtc: state.LastAlertAtUtc);
            }

            var lastAlertAtUtc = state.LastAlertAtUtc;
            var cooldown = TimeSpan.FromMinutes(Math.Max(1, _options.CooldownMinutes));
            var suppressedByCooldown = !force &&
                                       lastAlertAtUtc is not null &&
                                       now - lastAlertAtUtc.Value < cooldown;
            if (suppressedByCooldown)
            {
                return new SecurityAlertEvaluationResult(
                    EvaluatedAtUtc: now,
                    WindowStartUtc: windowStart,
                    HighCount: highCount,
                    MediumCount: mediumCount,
                    ThresholdExceeded: true,
                    AlertTriggered: false,
                    SuppressedByCooldown: true,
                    Disabled: !_options.Enabled,
                    AlertLevel: level,
                    Reason: $"{reason};cooldown_active",
                    LastAlertAtUtc: lastAlertAtUtc);
            }

            state.SetAlert(now, level, reason);
            logger.LogWarning(
                "Security alert triggered level={Level}, reason={Reason}, highCount={HighCount}, mediumCount={MediumCount}, windowMinutes={WindowMinutes}, source={Source}",
                level,
                reason,
                highCount,
                mediumCount,
                _options.WindowMinutes,
                source);

            var detailsJson = JsonSerializer.Serialize(new
            {
                reason,
                source,
                highCount,
                mediumCount,
                _options.WindowMinutes,
                _options.HighThreshold,
                _options.MediumThreshold
            });
            await store.AddSecurityEventAsync(
                eventType: "security_alert_triggered",
                severity: level,
                source: "security_alert_evaluator",
                accountId: null,
                matchSessionId: null,
                ipAddress: null,
                detailsJson: detailsJson,
                createdAtUtc: now,
                cancellationToken);

            return new SecurityAlertEvaluationResult(
                EvaluatedAtUtc: now,
                WindowStartUtc: windowStart,
                HighCount: highCount,
                MediumCount: mediumCount,
                ThresholdExceeded: true,
                AlertTriggered: true,
                SuppressedByCooldown: false,
                Disabled: !_options.Enabled,
                AlertLevel: level,
                Reason: reason,
                LastAlertAtUtc: now);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            state.SetFailure(DateTimeOffset.UtcNow, $"{ex.GetType().Name}: {ex.Message}");
            logger.LogError(ex, "Security alert evaluation failed.");
            throw;
        }
    }
}

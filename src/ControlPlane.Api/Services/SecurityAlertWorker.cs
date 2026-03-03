using ControlPlane.Api.Options;
using ControlPlane.Api.Persistence;
using Microsoft.Extensions.Options;

namespace ControlPlane.Api.Services;

public sealed class SecurityAlertWorker(
    ISqliteStore store,
    IOptions<SecurityAlertOptions> optionsAccessor,
    SecurityAlertState state,
    ILogger<SecurityAlertWorker> logger) : BackgroundService
{
    private readonly SecurityAlertOptions _options = optionsAccessor.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Security alert worker disabled.");
            return;
        }

        await EvaluateAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.SweepIntervalSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await EvaluateAsync(stoppingToken);
        }
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var sinceUtc = now.AddMinutes(-Math.Max(1, _options.WindowMinutes));
            var summary = await store.GetSecurityEventSummaryAsync(sinceUtc, cancellationToken);

            var highCount = summary
                .Where(row => string.Equals(row.Severity, "high", StringComparison.OrdinalIgnoreCase))
                .Sum(row => row.Count);
            var mediumCount = summary
                .Where(row => string.Equals(row.Severity, "medium", StringComparison.OrdinalIgnoreCase))
                .Sum(row => row.Count);
            state.SetEvaluation(now, highCount, mediumCount);

            var shouldAlert = false;
            var level = "none";
            var reason = "within_thresholds";
            if (highCount >= Math.Max(1, _options.HighThreshold))
            {
                shouldAlert = true;
                level = "high";
                reason = $"high_count={highCount} threshold={_options.HighThreshold}";
            }
            else if (mediumCount >= Math.Max(1, _options.MediumThreshold))
            {
                shouldAlert = true;
                level = "medium";
                reason = $"medium_count={mediumCount} threshold={_options.MediumThreshold}";
            }

            if (!shouldAlert)
            {
                return;
            }

            var cooldown = TimeSpan.FromMinutes(Math.Max(1, _options.CooldownMinutes));
            var lastAlertAtUtc = state.LastAlertAtUtc;
            if (lastAlertAtUtc is not null && now - lastAlertAtUtc.Value < cooldown)
            {
                return;
            }

            state.SetAlert(now, level, reason);
            logger.LogWarning(
                "Security alert triggered level={Level}, reason={Reason}, highCount={HighCount}, mediumCount={MediumCount}, windowMinutes={WindowMinutes}",
                level,
                reason,
                highCount,
                mediumCount,
                _options.WindowMinutes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            state.SetFailure(DateTimeOffset.UtcNow, $"{ex.GetType().Name}: {ex.Message}");
            logger.LogError(ex, "Security alert evaluation failed.");
        }
    }
}

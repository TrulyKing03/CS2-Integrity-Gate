using ControlPlane.Api.Options;
using ControlPlane.Api.Persistence;
using Microsoft.Extensions.Options;

namespace ControlPlane.Api.Services;

public sealed class RetentionCleanupWorker(
    ISqliteStore store,
    IOptions<DataRetentionOptions> optionsAccessor,
    RetentionCleanupState state,
    ILogger<RetentionCleanupWorker> logger) : BackgroundService
{
    private readonly DataRetentionOptions _options = optionsAccessor.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Retention cleanup worker disabled.");
            return;
        }

        if (_options.RunOnStartup)
        {
            await RunCleanupAsync(stoppingToken);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, _options.SweepIntervalMinutes)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunCleanupAsync(stoppingToken);
        }
    }

    private async Task RunCleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await store.CleanupExpiredOperationalDataAsync(
                DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(Math.Max(1, _options.JoinTokenRetentionMinutes)),
                TimeSpan.FromHours(Math.Max(1, _options.HeartbeatRetentionHours)),
                TimeSpan.FromHours(Math.Max(1, _options.TelemetryRetentionHours)),
                cancellationToken);
            state.SetSuccess(result);

            var total = result.ExpiredAccountSessionsDeleted +
                        result.ExpiredJoinTokensDeleted +
                        result.ExpiredHeartbeatsDeleted +
                        result.ExpiredTelemetryDeleted;
            if (total > 0)
            {
                logger.LogInformation(
                    "Retention cleanup deleted: sessions={Sessions}, joinTokens={JoinTokens}, heartbeats={Heartbeats}, telemetry={Telemetry}",
                    result.ExpiredAccountSessionsDeleted,
                    result.ExpiredJoinTokensDeleted,
                    result.ExpiredHeartbeatsDeleted,
                    result.ExpiredTelemetryDeleted);
            }
            else
            {
                logger.LogDebug("Retention cleanup finished. No expired records deleted.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            state.SetFailure(DateTimeOffset.UtcNow, $"{ex.GetType().Name}: {ex.Message}");
            logger.LogError(ex, "Retention cleanup execution failed.");
        }
    }
}

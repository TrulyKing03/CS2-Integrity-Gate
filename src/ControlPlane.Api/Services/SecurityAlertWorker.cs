using ControlPlane.Api.Options;
using Microsoft.Extensions.Options;

namespace ControlPlane.Api.Services;

public sealed class SecurityAlertWorker(
    ISecurityAlertEvaluator evaluator,
    IOptions<SecurityAlertOptions> optionsAccessor,
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
            await evaluator.EvaluateAsync(
                source: "worker",
                force: false,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Security alert evaluation failed.");
        }
    }
}

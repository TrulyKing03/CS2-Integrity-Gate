namespace Cs2.Plugin.CounterStrikeSharp;

/// <summary>
/// Integration skeleton for wiring <see cref="PluginRuntime"/> into an actual CounterStrikeSharp plugin.
/// Keep this class framework-agnostic until CounterStrikeSharp runtime references are added in deployment.
/// </summary>
public sealed class CounterStrikeSharpAdapterSkeleton
{
    private readonly PluginRuntime _runtime;

    public CounterStrikeSharpAdapterSkeleton(PluginRuntime runtime)
    {
        _runtime = runtime;
    }

    public Task OnPlayerConnectAttemptAsync(PlayerConnectionAttempt attempt, CancellationToken cancellationToken)
    {
        return _runtime.HandleConnectionAttemptAsync(attempt, cancellationToken);
    }

    public Task OnTickAsync(TickSample sample, CancellationToken cancellationToken)
    {
        return _runtime.CaptureTickAsync(sample, cancellationToken);
    }

    public Task OnShotAsync(ShotSample sample, CancellationToken cancellationToken)
    {
        return _runtime.CaptureShotAsync(sample, cancellationToken);
    }

    public Task OnVisibilitySampleAsync(VisibilitySample sample, CancellationToken cancellationToken)
    {
        return _runtime.CaptureVisibilityAsync(sample, cancellationToken);
    }

    public Task OnRoundBoundaryAsync(string matchSessionId, CancellationToken cancellationToken)
    {
        return _runtime.FlushTelemetryAsync(matchSessionId, cancellationToken);
    }

    public Task OnHealthPollTickAsync(string matchSessionId, CancellationToken cancellationToken)
    {
        return _runtime.PollHealthAndEnforceAsync(matchSessionId, cancellationToken);
    }

    public Task OnActionPollTickAsync(string matchSessionId, string? accountId, CancellationToken cancellationToken)
    {
        return _runtime.PollPendingActionsAndApplyAsync(matchSessionId, accountId, cancellationToken);
    }
}

namespace Cs2.Plugin.CounterStrikeSharp;

/// <summary>
/// Integration skeleton for wiring <see cref="PluginRuntime"/> into an actual CounterStrikeSharp plugin.
/// Keep this class framework-agnostic until CounterStrikeSharp runtime references are added in deployment.
/// </summary>
public sealed class CounterStrikeSharpAdapterSkeleton
{
    private readonly PluginRuntime _runtime;
    private readonly MatchRuntimeCoordinator _coordinator;

    public CounterStrikeSharpAdapterSkeleton(PluginRuntime runtime, MatchRuntimeCoordinator coordinator)
    {
        _runtime = runtime;
        _coordinator = coordinator;
    }

    public Task OnPlayerConnectAttemptAsync(PlayerConnectionAttempt attempt, CancellationToken cancellationToken)
    {
        return _runtime.HandleConnectionAttemptAsync(attempt, cancellationToken);
    }

    public void OnPlayerConnected(PlayerSessionIdentity session)
    {
        _coordinator.TrackPlayer(session);
    }

    public Task OnPlayerDisconnectedAsync(PlayerSessionIdentity session)
    {
        return _coordinator.UntrackPlayerAsync(session);
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
}

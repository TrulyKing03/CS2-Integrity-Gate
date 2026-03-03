using System.Collections.Concurrent;
using Shared.Contracts;

namespace Cs2.Plugin.CounterStrikeSharp;

public sealed class PluginRuntime : IDisposable
{
    private readonly PluginRuntimeOptions _options;
    private readonly IPluginHostBridge _hostBridge;
    private readonly ControlPlanePluginClient _client;
    private readonly bool _ownsClient;
    private readonly ConcurrentDictionary<string, TelemetryBuffer> _buffers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastFlushUtc = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _lastHealthStatus = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActionExecutionState> _actionExecution = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    public PluginRuntime(
        PluginRuntimeOptions options,
        IPluginHostBridge hostBridge,
        ControlPlanePluginClient? client = null)
    {
        _options = options;
        _hostBridge = hostBridge;
        _client = client ?? new ControlPlanePluginClient(options);
        _ownsClient = client is null;
    }

    public async Task HandleConnectionAttemptAsync(PlayerConnectionAttempt attempt, CancellationToken cancellationToken)
    {
        try
        {
            var validation = await _client.ValidateJoinAsync(
                new ValidateJoinRequest(
                    attempt.ServerId,
                    attempt.SteamId,
                    attempt.AccountId,
                    attempt.MatchSessionId,
                    attempt.JoinToken),
                cancellationToken);

            if (!validation.Allow)
            {
                await _hostBridge.DenyConnectionAsync(attempt, validation.Reason, cancellationToken);
                _hostBridge.LogWarning(
                    $"Denied join: account={attempt.AccountId}, match={attempt.MatchSessionId}, reason={validation.Reason}");
                return;
            }

            await _hostBridge.AcceptConnectionAsync(attempt, cancellationToken);
            _hostBridge.LogInfo(
                $"Accepted join: account={attempt.AccountId}, match={attempt.MatchSessionId}, trust={validation.TrustTier}");
        }
        catch (Exception ex)
        {
            _hostBridge.LogError(
                $"Join validation error for account={attempt.AccountId}, match={attempt.MatchSessionId}",
                ex);
            await _hostBridge.DenyConnectionAsync(attempt, "validation_error", cancellationToken);
        }
    }

    public async Task CaptureTickAsync(TickSample sample, CancellationToken cancellationToken)
    {
        var buffer = GetBuffer(sample.MatchSessionId);
        buffer.AddTick(sample.ToContract());
        await MaybeFlushAsync(sample.MatchSessionId, force: false, cancellationToken);
    }

    public async Task CaptureShotAsync(ShotSample sample, CancellationToken cancellationToken)
    {
        var buffer = GetBuffer(sample.MatchSessionId);
        buffer.AddShot(sample.ToContract());
        await MaybeFlushAsync(sample.MatchSessionId, force: false, cancellationToken);
    }

    public async Task CaptureVisibilityAsync(VisibilitySample sample, CancellationToken cancellationToken)
    {
        var buffer = GetBuffer(sample.MatchSessionId);
        buffer.AddLos(sample.ToContract());
        await MaybeFlushAsync(sample.MatchSessionId, force: false, cancellationToken);
    }

    public Task FlushTelemetryAsync(string matchSessionId, CancellationToken cancellationToken)
    {
        return MaybeFlushAsync(matchSessionId, force: true, cancellationToken);
    }

    public async Task PollHealthAndEnforceAsync(
        string matchSessionId,
        CancellationToken cancellationToken)
    {
        var health = await _client.GetMatchHealthAsync(matchSessionId, cancellationToken);
        foreach (var player in health.Players)
        {
            var key = $"{matchSessionId}|{player.AccountId}";
            var previous = _lastHealthStatus.GetOrAdd(key, "unknown");
            _lastHealthStatus[key] = player.Status;

            if (string.Equals(player.Status, "healthy", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var transitioned = !string.Equals(previous, player.Status, StringComparison.OrdinalIgnoreCase);
            if (!transitioned)
            {
                continue;
            }

            var action = new EnforcementAction(
                Guid.NewGuid().ToString("N"),
                matchSessionId,
                player.AccountId,
                ActionType: "kick",
                ReasonCode: "heartbeat_unhealthy",
                DurationSeconds: 0,
                CreatedAtUtc: DateTimeOffset.UtcNow);
            await _hostBridge.ApplyEnforcementActionAsync(action, cancellationToken);
            _hostBridge.LogWarning(
                $"Applied health action: account={player.AccountId}, match={matchSessionId}, status={player.Status}");
        }
    }

    public async Task PollPendingActionsAndApplyAsync(
        string matchSessionId,
        string? accountId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        TrimActionExecutionCache(now);

        var fetchLimit = Math.Max(1, _options.PendingActionFetchLimit);
        var actions = await _client.GetPendingActionsAsync(matchSessionId, accountId, fetchLimit, cancellationToken);
        foreach (var action in actions)
        {
            var state = _actionExecution.GetOrAdd(
                action.ActionId,
                _ => new ActionExecutionState(
                    Result: "pending",
                    Notes: "pending",
                    AppliedAtUtc: DateTimeOffset.MinValue,
                    LastSeenUtc: now,
                    LastAckAttemptUtc: DateTimeOffset.MinValue,
                    AckAttempts: 0));

            state = state with { LastSeenUtc = now };

            if (string.Equals(state.Result, "pending", StringComparison.OrdinalIgnoreCase))
            {
                var result = "applied";
                var notes = "Applied by Cs2.Plugin.CounterStrikeSharp runtime";
                try
                {
                    await _hostBridge.ApplyEnforcementActionAsync(action, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result = "failed";
                    notes = $"Host bridge failed: {ex.GetType().Name}: {ex.Message}";
                    _hostBridge.LogError(
                        $"Action apply failed: actionId={action.ActionId}, account={action.AccountId}",
                        ex);
                }

                state = state with
                {
                    Result = result,
                    Notes = notes,
                    AppliedAtUtc = now,
                    LastSeenUtc = now
                };
                _actionExecution[action.ActionId] = state;
            }

            if (state.LastAckAttemptUtc != DateTimeOffset.MinValue &&
                now - state.LastAckAttemptUtc < TimeSpan.FromSeconds(Math.Max(1, _options.ActionAckRetrySec)))
            {
                continue;
            }

            state = state with
            {
                LastAckAttemptUtc = now,
                LastSeenUtc = now,
                AckAttempts = state.AckAttempts + 1
            };
            _actionExecution[action.ActionId] = state;

            try
            {
                var ack = await _client.AckActionAsync(
                    new EnforcementActionAckRequest(
                        action.ActionId,
                        action.MatchSessionId,
                        action.AccountId,
                        _options.ExecutorId,
                        Result: state.Result,
                        Notes: state.Notes,
                        AckedAtUtc: DateTimeOffset.UtcNow),
                    cancellationToken);
                _hostBridge.LogInfo(
                    $"Action ack: actionId={action.ActionId}, accepted={ack.Accepted}, reason={ack.Reason}, result={state.Result}, attempts={state.AckAttempts}");

                if (ack.Accepted ||
                    string.Equals(ack.Reason, "already_acked", StringComparison.OrdinalIgnoreCase))
                {
                    _actionExecution.TryRemove(action.ActionId, out _);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _hostBridge.LogError(
                    $"Action ack failed: actionId={action.ActionId}, result={state.Result}, attempts={state.AckAttempts}",
                    ex);
            }
        }
    }

    public void Dispose()
    {
        _flushGate.Dispose();
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private TelemetryBuffer GetBuffer(string matchSessionId)
    {
        return _buffers.GetOrAdd(matchSessionId, _ => new TelemetryBuffer());
    }

    private async Task MaybeFlushAsync(string matchSessionId, bool force, CancellationToken cancellationToken)
    {
        var buffer = GetBuffer(matchSessionId);
        var now = DateTimeOffset.UtcNow;
        var previousFlush = _lastFlushUtc.GetOrAdd(matchSessionId, DateTimeOffset.MinValue);

        var dueBySize = buffer.ApproximateQueuedCount >= _options.MaxBatchSize;
        var dueByTime = now - previousFlush >= TimeSpan.FromSeconds(_options.TelemetryFlushSec);
        if (!force && !dueBySize && !dueByTime)
        {
            return;
        }

        await _flushGate.WaitAsync(cancellationToken);
        try
        {
            var drained = buffer.Drain(_options.MaxBatchSize);
            if (drained.Ticks.Count == 0 &&
                drained.Shots.Count == 0 &&
                drained.LosSamples.Count == 0)
            {
                return;
            }

            try
            {
                await _client.PostTicksAsync(matchSessionId, _options.TelemetrySource, drained.Ticks, cancellationToken);
                await _client.PostShotsAsync(matchSessionId, _options.TelemetrySource, drained.Shots, cancellationToken);
                await _client.PostLosSamplesAsync(matchSessionId, _options.TelemetrySource, drained.LosSamples, cancellationToken);
                _lastFlushUtc[matchSessionId] = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                // Keep telemetry in process by re-queueing batches when backend calls fail.
                foreach (var item in drained.Ticks)
                {
                    buffer.AddTick(item);
                }

                foreach (var item in drained.Shots)
                {
                    buffer.AddShot(item);
                }

                foreach (var item in drained.LosSamples)
                {
                    buffer.AddLos(item);
                }

                _hostBridge.LogError(
                    $"Telemetry flush failed for match={matchSessionId}.",
                    ex);
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private void TrimActionExecutionCache(DateTimeOffset now)
    {
        var dedupeWindow = TimeSpan.FromSeconds(Math.Max(30, _options.ActionApplyDedupeSec));
        foreach (var pair in _actionExecution)
        {
            if (now - pair.Value.LastSeenUtc <= dedupeWindow)
            {
                continue;
            }

            _actionExecution.TryRemove(pair.Key, out _);
        }
    }

    private sealed record ActionExecutionState(
        string Result,
        string Notes,
        DateTimeOffset AppliedAtUtc,
        DateTimeOffset LastSeenUtc,
        DateTimeOffset LastAckAttemptUtc,
        int AckAttempts);
}

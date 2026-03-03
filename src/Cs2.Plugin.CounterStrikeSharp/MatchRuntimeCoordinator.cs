using System.Collections.Concurrent;

namespace Cs2.Plugin.CounterStrikeSharp;

/// <summary>
/// Runs periodic health/action polling workers per active match session.
/// A real plugin should track connected players and forward connect/disconnect to this coordinator.
/// </summary>
public sealed class MatchRuntimeCoordinator : IAsyncDisposable
{
    private readonly PluginRuntime _runtime;
    private readonly IPluginHostBridge _hostBridge;
    private readonly PluginRuntimeOptions _options;
    private readonly ConcurrentDictionary<string, MatchWorker> _workers = new(StringComparer.Ordinal);

    public MatchRuntimeCoordinator(
        PluginRuntime runtime,
        IPluginHostBridge hostBridge,
        PluginRuntimeOptions options)
    {
        _runtime = runtime;
        _hostBridge = hostBridge;
        _options = options;
    }

    public void TrackPlayer(PlayerSessionIdentity session)
    {
        var worker = _workers.GetOrAdd(
            session.MatchSessionId,
            matchId => new MatchWorker(matchId, _runtime, _hostBridge, _options));

        worker.Track(session);
    }

    public async Task UntrackPlayerAsync(PlayerSessionIdentity session)
    {
        if (!_workers.TryGetValue(session.MatchSessionId, out var worker))
        {
            return;
        }

        worker.Untrack(session);
        if (worker.PlayerCount != 0)
        {
            return;
        }

        if (_workers.TryRemove(session.MatchSessionId, out var removed))
        {
            await removed.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var workers = _workers.Values.ToArray();
        _workers.Clear();
        foreach (var worker in workers)
        {
            await worker.DisposeAsync();
        }
    }

    private sealed class MatchWorker : IAsyncDisposable
    {
        private readonly string _matchSessionId;
        private readonly PluginRuntime _runtime;
        private readonly IPluginHostBridge _hostBridge;
        private readonly PluginRuntimeOptions _options;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<string, PlayerSessionIdentity> _players = new(StringComparer.Ordinal);
        private readonly Task _loopTask;

        public MatchWorker(
            string matchSessionId,
            PluginRuntime runtime,
            IPluginHostBridge hostBridge,
            PluginRuntimeOptions options)
        {
            _matchSessionId = matchSessionId;
            _runtime = runtime;
            _hostBridge = hostBridge;
            _options = options;
            _loopTask = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
        }

        public int PlayerCount => _players.Count;

        public void Track(PlayerSessionIdentity session)
        {
            _players[$"{session.AccountId}|{session.SteamId}"] = session;
            _hostBridge.LogInfo(
                $"Match worker track: match={_matchSessionId}, account={session.AccountId}, players={_players.Count}");
        }

        public void Untrack(PlayerSessionIdentity session)
        {
            _players.TryRemove($"{session.AccountId}|{session.SteamId}", out _);
            _hostBridge.LogInfo(
                $"Match worker untrack: match={_matchSessionId}, account={session.AccountId}, players={_players.Count}");
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _loopTask;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _cts.Dispose();
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            _hostBridge.LogInfo($"Match worker started: match={_matchSessionId}");
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            var nextHealth = DateTimeOffset.UtcNow;
            var nextAction = DateTimeOffset.UtcNow;

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (_players.IsEmpty)
                {
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                if (now >= nextHealth)
                {
                    await SafeRun(
                        () => _runtime.PollHealthAndEnforceAsync(_matchSessionId, cancellationToken),
                        "health_poll");
                    nextHealth = now.AddSeconds(Math.Max(1, _options.HealthPollSec));
                }

                if (now >= nextAction)
                {
                    await SafeRun(
                        () => _runtime.PollPendingActionsAndApplyAsync(_matchSessionId, accountId: null, cancellationToken),
                        "action_poll");
                    nextAction = now.AddSeconds(Math.Max(1, _options.ActionPollSec));
                }
            }

            _hostBridge.LogInfo($"Match worker stopped: match={_matchSessionId}");
        }

        private async Task SafeRun(Func<Task> action, string operation)
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _hostBridge.LogError(
                    $"Match worker {operation} failure for match={_matchSessionId}",
                    ex);
            }
        }
    }
}

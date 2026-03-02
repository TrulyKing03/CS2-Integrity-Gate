using Shared.Contracts;

namespace Cs2.Plugin.CounterStrikeSharp;

internal sealed class TelemetryBuffer
{
    private readonly object _lock = new();
    private readonly List<TickPlayerState> _ticks = new();
    private readonly List<ShotEvent> _shots = new();
    private readonly List<LosSample> _losSamples = new();

    public void AddTick(TickPlayerState tick)
    {
        lock (_lock)
        {
            _ticks.Add(tick);
        }
    }

    public void AddShot(ShotEvent shot)
    {
        lock (_lock)
        {
            _shots.Add(shot);
        }
    }

    public void AddLos(LosSample sample)
    {
        lock (_lock)
        {
            _losSamples.Add(sample);
        }
    }

    public (IReadOnlyList<TickPlayerState> Ticks, IReadOnlyList<ShotEvent> Shots, IReadOnlyList<LosSample> LosSamples) Drain(int maxBatchSize)
    {
        lock (_lock)
        {
            var ticks = DrainList(_ticks, maxBatchSize);
            var shots = DrainList(_shots, maxBatchSize);
            var los = DrainList(_losSamples, maxBatchSize);
            return (ticks, shots, los);
        }
    }

    public int ApproximateQueuedCount
    {
        get
        {
            lock (_lock)
            {
                return _ticks.Count + _shots.Count + _losSamples.Count;
            }
        }
    }

    private static IReadOnlyList<T> DrainList<T>(List<T> source, int maxBatchSize)
    {
        if (source.Count == 0)
        {
            return Array.Empty<T>();
        }

        var take = Math.Min(maxBatchSize, source.Count);
        var items = source.GetRange(0, take);
        source.RemoveRange(0, take);
        return items;
    }
}

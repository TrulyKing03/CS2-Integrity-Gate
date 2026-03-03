using ControlPlane.Api.Persistence;

namespace ControlPlane.Api.Services;

public sealed class RetentionCleanupState
{
    private readonly object _gate = new();
    private DataCleanupResult? _lastResult;
    private DateTimeOffset? _lastFailureAtUtc;
    private string? _lastFailureMessage;

    public void SetSuccess(DataCleanupResult result)
    {
        lock (_gate)
        {
            _lastResult = result;
            _lastFailureAtUtc = null;
            _lastFailureMessage = null;
        }
    }

    public void SetFailure(DateTimeOffset failureAtUtc, string message)
    {
        lock (_gate)
        {
            _lastFailureAtUtc = failureAtUtc;
            _lastFailureMessage = message;
        }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                lastResult = _lastResult,
                lastFailureAtUtc = _lastFailureAtUtc,
                lastFailureMessage = _lastFailureMessage
            };
        }
    }
}

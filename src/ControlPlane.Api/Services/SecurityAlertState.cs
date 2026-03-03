namespace ControlPlane.Api.Services;

public sealed class SecurityAlertState
{
    private readonly object _gate = new();
    private DateTimeOffset? _lastEvaluatedAtUtc;
    private DateTimeOffset? _lastAlertAtUtc;
    private string? _lastAlertLevel;
    private string? _lastAlertReason;
    private int _lastHighCount;
    private int _lastMediumCount;
    private DateTimeOffset? _lastFailureAtUtc;
    private string? _lastFailureMessage;

    public DateTimeOffset? LastAlertAtUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastAlertAtUtc;
            }
        }
    }

    public void SetEvaluation(DateTimeOffset evaluatedAtUtc, int highCount, int mediumCount)
    {
        lock (_gate)
        {
            _lastEvaluatedAtUtc = evaluatedAtUtc;
            _lastHighCount = highCount;
            _lastMediumCount = mediumCount;
            _lastFailureAtUtc = null;
            _lastFailureMessage = null;
        }
    }

    public void SetAlert(DateTimeOffset alertAtUtc, string level, string reason)
    {
        lock (_gate)
        {
            _lastAlertAtUtc = alertAtUtc;
            _lastAlertLevel = level;
            _lastAlertReason = reason;
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
                lastEvaluatedAtUtc = _lastEvaluatedAtUtc,
                lastAlertAtUtc = _lastAlertAtUtc,
                lastAlertLevel = _lastAlertLevel,
                lastAlertReason = _lastAlertReason,
                lastHighCount = _lastHighCount,
                lastMediumCount = _lastMediumCount,
                lastFailureAtUtc = _lastFailureAtUtc,
                lastFailureMessage = _lastFailureMessage
            };
        }
    }
}

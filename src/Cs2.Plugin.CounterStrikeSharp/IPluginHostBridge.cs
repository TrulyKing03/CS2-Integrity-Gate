using Shared.Contracts;

namespace Cs2.Plugin.CounterStrikeSharp;

public interface IPluginHostBridge
{
    Task DenyConnectionAsync(PlayerConnectionAttempt attempt, string reason, CancellationToken cancellationToken);
    Task AcceptConnectionAsync(PlayerConnectionAttempt attempt, CancellationToken cancellationToken);
    Task ApplyEnforcementActionAsync(EnforcementAction action, CancellationToken cancellationToken);
    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? exception = null);
}

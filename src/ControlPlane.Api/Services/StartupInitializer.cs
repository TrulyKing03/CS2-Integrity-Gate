using ControlPlane.Api.Persistence;

namespace ControlPlane.Api.Services;

public sealed class StartupInitializer(
    ISqliteStore sqliteStore,
    ILogger<StartupInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await sqliteStore.InitializeAsync(cancellationToken);
        logger.LogInformation("SQLite store initialized.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

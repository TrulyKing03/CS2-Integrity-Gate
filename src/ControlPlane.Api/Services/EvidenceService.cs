using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlPlane.Api.Options;
using ControlPlane.Api.Persistence;
using Microsoft.Extensions.Options;
using Shared.Contracts;

namespace ControlPlane.Api.Services;

public interface IEvidenceService
{
    Task<EvidencePackSummary> BuildAndStoreEvidenceAsync(
        string matchSessionId,
        string accountId,
        string triggerType,
        string? actionId,
        CancellationToken cancellationToken);
}

public sealed class EvidenceService(
    ISqliteStore sqliteStore,
    IOptions<EvidenceOptions> evidenceOptions) : IEvidenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<EvidencePackSummary> BuildAndStoreEvidenceAsync(
        string matchSessionId,
        string accountId,
        string triggerType,
        string? actionId,
        CancellationToken cancellationToken)
    {
        var options = evidenceOptions.Value;
        var telemetry = await sqliteStore.GetRecentTelemetryEventsAsync(
            matchSessionId,
            accountId,
            options.RecentTelemetryLimit,
            cancellationToken);
        var heartbeats = await sqliteStore.GetRecentHeartbeatsAsync(
            matchSessionId,
            accountId,
            options.RecentHeartbeatsLimit,
            cancellationToken);
        var scores = await sqliteStore.GetSuspicionScoresAsync(matchSessionId, accountId, cancellationToken);

        var evidenceId = $"ev_{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.UtcNow;

        var payload = new
        {
            evidenceId,
            matchSessionId,
            accountId,
            triggerType,
            actionId,
            createdAtUtc = createdAt,
            telemetryEvents = telemetry,
            heartbeats,
            scores
        };

        var fullDir = Path.GetFullPath(options.StorageDirectory);
        Directory.CreateDirectory(fullDir);
        var fileName = $"{evidenceId}.json";
        var fullPath = Path.Combine(fullDir, fileName);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await File.WriteAllTextAsync(fullPath, json, cancellationToken);

        var hash = Sha256(json);
        var summary = new EvidencePackSummary(
            evidenceId,
            matchSessionId,
            accountId,
            triggerType,
            actionId,
            fullPath,
            hash,
            createdAt,
            "pending_review");

        await sqliteStore.SaveEvidencePackSummaryAsync(summary, cancellationToken);
        return summary;
    }

    private static string Sha256(string text)
    {
        var data = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(data).ToLowerInvariant();
    }
}

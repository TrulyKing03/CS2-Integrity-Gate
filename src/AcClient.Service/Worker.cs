using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AcClient.Service.Options;
using Microsoft.Extensions.Options;
using Shared.Contracts;

namespace AcClient.Service;

public sealed class Worker(
    ILogger<Worker> logger,
    IHttpClientFactory httpClientFactory,
    IOptions<AcClientOptions> optionsAccessor) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AcClientOptions _options = optionsAccessor.Value;
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient("control-plane");
    private string? _deviceId;
    private string? _currentMatchSessionId;
    private string? _currentServerId;
    private int _heartbeatIntervalSec = 10;
    private long _heartbeatSequence;
    private DateTimeOffset _lastHeartbeatUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _httpClient.BaseAddress = new Uri(_options.BackendBaseUrl.TrimEnd('/') + "/");
        EnsureDirectories();

        var publicKeyPem = await EnsureDeviceKeyAsync(_options.DeviceKeyPath, stoppingToken);
        _deviceId = await EnsureEnrolledAsync(publicKeyPem, stoppingToken);
        logger.LogInformation("AC client enrolled with DeviceId={DeviceId}", _deviceId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var session = await LoadSessionAsync(stoppingToken);
                if (session is null)
                {
                    await HandleNoSessionAsync(stoppingToken);
                }
                else
                {
                    await HandleSessionAsync(session, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AC loop iteration failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task HandleNoSessionAsync(CancellationToken cancellationToken)
    {
        if (_currentMatchSessionId is null)
        {
            return;
        }

        logger.LogInformation("No active session file. Clearing AC runtime state.");
        _currentMatchSessionId = null;
        _currentServerId = null;
        _lastHeartbeatUtc = DateTimeOffset.MinValue;
        _heartbeatSequence = 0;
        _heartbeatIntervalSec = 10;
        if (File.Exists(_options.JoinTokenOutputPath))
        {
            File.Delete(_options.JoinTokenOutputPath);
        }

        await Task.CompletedTask;
        _ = cancellationToken;
    }

    private async Task HandleSessionAsync(QueueSessionState session, CancellationToken cancellationToken)
    {
        if (_deviceId is null)
        {
            throw new InvalidOperationException("Device enrollment was not completed.");
        }

        if (!string.Equals(_currentMatchSessionId, session.MatchSessionId, StringComparison.Ordinal))
        {
            await BeginMatchAsync(session, _deviceId, cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastHeartbeatUtc >= TimeSpan.FromSeconds(_heartbeatIntervalSec))
        {
            await SendHeartbeatAsync(session, _deviceId, cancellationToken);
        }
    }

    private async Task BeginMatchAsync(
        QueueSessionState session,
        string deviceId,
        CancellationToken cancellationToken)
    {
        _currentMatchSessionId = session.MatchSessionId;
        _currentServerId = session.ServerId;
        _heartbeatSequence = 0;
        _lastHeartbeatUtc = DateTimeOffset.MinValue;
        logger.LogInformation("Starting AC match mode for MatchSessionId={MatchSessionId}", session.MatchSessionId);

        var request = new MatchStartRequest(
            session.MatchSessionId,
            session.AccountId,
            session.SteamId,
            deviceId,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            PlatformSignals(),
            IntegritySignals());

        var response = await _httpClient.PostAsJsonAsync("v1/attestation/match-start", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"match-start failed: {(int)response.StatusCode} {body}");
        }

        var matchStart = await response.Content.ReadFromJsonAsync<MatchStartResponse>(JsonOptions, cancellationToken);
        if (matchStart is null)
        {
            throw new InvalidOperationException("match-start response was empty");
        }

        _heartbeatIntervalSec = matchStart.HeartbeatIntervalSec;
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(matchStart.JoinTokenTtlSec);
        var joinTokenFile = new JoinTokenRuntimeFile(
            session.MatchSessionId,
            session.AccountId,
            session.SteamId,
            session.ServerId,
            matchStart.JoinToken,
            expiresAt);
        await File.WriteAllTextAsync(
            _options.JoinTokenOutputPath,
            JsonSerializer.Serialize(joinTokenFile, JsonOptions),
            cancellationToken);

        logger.LogInformation("Join token acquired for MatchSessionId={MatchSessionId} (TTL={TtlSec}s)",
            session.MatchSessionId,
            matchStart.JoinTokenTtlSec);

        await SendHeartbeatAsync(session, deviceId, cancellationToken);
    }

    private async Task SendHeartbeatAsync(
        QueueSessionState session,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var request = new HeartbeatRequest(
            session.MatchSessionId,
            session.AccountId,
            deviceId,
            Interlocked.Increment(ref _heartbeatSequence),
            PlatformSignals(),
            IntegritySignals(),
            HeartbeatLatencyMs());

        var response = await _httpClient.PostAsJsonAsync("v1/attestation/heartbeat", request, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("heartbeat failed: status={StatusCode}, body={Body}", (int)response.StatusCode, body);
            return;
        }

        var heartbeat = await response.Content.ReadFromJsonAsync<HeartbeatResponse>(JsonOptions, cancellationToken);
        if (heartbeat is null)
        {
            logger.LogWarning("heartbeat response was empty.");
            return;
        }

        _lastHeartbeatUtc = DateTimeOffset.UtcNow;
        _heartbeatIntervalSec = Math.Max(5, heartbeat.NextIntervalSec);
        if (!string.Equals(heartbeat.Status, "healthy", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("heartbeat returned unhealthy status. Hint={Hint}", heartbeat.ServerActionHint);
        }
    }

    private async Task<string> EnsureEnrolledAsync(string publicKeyPem, CancellationToken cancellationToken)
    {
        var request = new EnrollRequest(
            _options.AccountId,
            _options.SteamId,
            publicKeyPem,
            _options.LauncherVersion,
            _options.AcVersion);

        var response = await _httpClient.PostAsJsonAsync("v1/attestation/enroll", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        var enroll = await response.Content.ReadFromJsonAsync<EnrollResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("enroll response was empty");
        return enroll.DeviceId;
    }

    private async Task<QueueSessionState?> LoadSessionAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.SessionFilePath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(_options.SessionFilePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<QueueSessionState>(json, JsonOptions);
    }

    private static PlatformSignals PlatformSignals() => new(
        SecureBoot: true,
        Tpm20: true,
        Iommu: true,
        Vbs: false);

    private IntegritySignals IntegritySignals() => new(
        AcServiceHealthy: true,
        DriverLoaded: true,
        ModulePolicyOk: true,
        AntiTamperOk: true,
        PolicyHash: _options.PolicyHash);

    private static int HeartbeatLatencyMs() => Random.Shared.Next(12, 55);

    private static async Task<string> EnsureDeviceKeyAsync(string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            var existingPem = await File.ReadAllTextAsync(path, cancellationToken);
            using var existingKey = ECDsa.Create();
            existingKey.ImportFromPem(existingPem);
            return existingKey.ExportSubjectPublicKeyInfoPem();
        }

        using var generatedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privatePem = generatedKey.ExportECPrivateKeyPem();
        await File.WriteAllTextAsync(path, privatePem, cancellationToken);
        return generatedKey.ExportSubjectPublicKeyInfoPem();
    }

    private void EnsureDirectories()
    {
        CreateDirectoryForPath(_options.SessionFilePath);
        CreateDirectoryForPath(_options.JoinTokenOutputPath);
        CreateDirectoryForPath(_options.DeviceKeyPath);
    }

    private static void CreateDirectoryForPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private sealed record JoinTokenRuntimeFile(
        string MatchSessionId,
        string AccountId,
        string SteamId,
        string ServerId,
        string JoinToken,
        DateTimeOffset ExpiresAtUtc);
}

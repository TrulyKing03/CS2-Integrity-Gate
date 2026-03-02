using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Shared.Contracts;

namespace Cs2.Plugin.CounterStrikeSharp;

public sealed class ControlPlanePluginClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public ControlPlanePluginClient(PluginRuntimeOptions options, HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        _ownsHttp = httpClient is null;
        _http.BaseAddress = new Uri(options.BackendBaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Remove("X-Server-Api-Key");
        _http.DefaultRequestHeaders.Add("X-Server-Api-Key", options.ServerApiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<ValidateJoinResponse> ValidateJoinAsync(ValidateJoinRequest request, CancellationToken cancellationToken)
    {
        var response = await _http.PostAsJsonAsync("v1/attestation/validate-join", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ValidateJoinResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("validate-join response was empty");
    }

    public async Task<MatchHealthResponse> GetMatchHealthAsync(string matchSessionId, CancellationToken cancellationToken)
    {
        var result = await _http.GetFromJsonAsync<MatchHealthResponse>(
            $"v1/attestation/match-health?matchSessionId={Uri.EscapeDataString(matchSessionId)}",
            JsonOptions,
            cancellationToken);
        return result ?? new MatchHealthResponse(matchSessionId, DateTimeOffset.UtcNow, Array.Empty<PlayerHealthState>());
    }

    public async Task PostTicksAsync(
        string matchSessionId,
        string source,
        IReadOnlyList<TickPlayerState> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        var payload = new TelemetryEnvelope<TickPlayerState>(matchSessionId, source, DateTimeOffset.UtcNow, items);
        var response = await _http.PostAsJsonAsync("v1/telemetry/ticks", payload, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task PostShotsAsync(
        string matchSessionId,
        string source,
        IReadOnlyList<ShotEvent> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        var payload = new TelemetryEnvelope<ShotEvent>(matchSessionId, source, DateTimeOffset.UtcNow, items);
        var response = await _http.PostAsJsonAsync("v1/telemetry/shots", payload, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task PostLosSamplesAsync(
        string matchSessionId,
        string source,
        IReadOnlyList<LosSample> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        var payload = new TelemetryEnvelope<LosSample>(matchSessionId, source, DateTimeOffset.UtcNow, items);
        var response = await _http.PostAsJsonAsync("v1/telemetry/los", payload, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<EnforcementAction>> GetPendingActionsAsync(
        string matchSessionId,
        string? accountId,
        CancellationToken cancellationToken)
    {
        var route = $"v1/enforcement/actions/{Uri.EscapeDataString(matchSessionId)}/pending";
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            route += $"?accountId={Uri.EscapeDataString(accountId)}";
        }

        var result = await _http.GetFromJsonAsync<List<EnforcementAction>>(route, JsonOptions, cancellationToken);
        return result is null ? Array.Empty<EnforcementAction>() : result;
    }

    public async Task<EnforcementActionAckResponse> AckActionAsync(EnforcementActionAckRequest request, CancellationToken cancellationToken)
    {
        var response = await _http.PostAsJsonAsync("v1/enforcement/actions/ack", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EnforcementActionAckResponse>(JsonOptions, cancellationToken)
            ?? new EnforcementActionAckResponse(false, "invalid_response", DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}

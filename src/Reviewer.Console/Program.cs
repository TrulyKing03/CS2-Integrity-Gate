using System.Net.Http.Json;
using System.Text.Json;
using Shared.Contracts;

var options = CliOptions.Parse(args);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

using var http = new HttpClient
{
    BaseAddress = new Uri(options.BackendBaseUrl.TrimEnd('/') + "/")
};
http.DefaultRequestHeaders.Add("X-Internal-Api-Key", options.InternalApiKey);

switch (options.Command)
{
    case "system-metrics":
    {
        var metrics = await http.GetFromJsonAsync<object>("v1/metrics/summary", json)
            ?? throw new InvalidOperationException("metrics response was empty");
        Print(metrics, json);
        break;
    }
    case "run-cleanup":
    {
        var response = await http.PostAsync("v1/ops/cleanup/run", content: null);
        await EnsureAndPrintAsync(response, json);
        break;
    }
    case "cleanup-status":
    {
        var status = await http.GetFromJsonAsync<object>("v1/ops/cleanup/status", json)
            ?? throw new InvalidOperationException("cleanup status response was empty");
        Print(status, json);
        break;
    }
    case "revoke-sessions":
    {
        var request = new RevokeAccountSessionsRequest(
            AccountId: options.Require("--account", options.AccountId),
            Reason: options.ReasonCode ?? "manual_revoke",
            RequestedBy: options.RequestedBy ?? "reviewer_console");
        var response = await http.PostAsJsonAsync("v1/ops/auth/sessions/revoke", request, json);
        await EnsureAndPrintAsync(response, json);
        break;
    }
    case "list-security-events":
    {
        var route = "v1/ops/security/events";
        route = Append(route, "sinceMinutes", options.SinceMinutes?.ToString());
        route = Append(route, "severity", options.Severity);
        route = Append(route, "eventType", options.EventType);
        route = Append(route, "limit", options.Limit?.ToString());
        var items = await http.GetFromJsonAsync<List<SecurityEventRecord>>(route, json)
            ?? new List<SecurityEventRecord>();
        Print(items, json);
        break;
    }
    case "security-summary":
    {
        var route = "v1/ops/security/summary";
        route = Append(route, "sinceMinutes", options.SinceMinutes?.ToString());
        var items = await http.GetFromJsonAsync<List<SecurityEventSummary>>(route, json)
            ?? new List<SecurityEventSummary>();
        Print(items, json);
        break;
    }
    case "security-alert-status":
    {
        var status = await http.GetFromJsonAsync<object>("v1/ops/security/alerts/status", json)
            ?? throw new InvalidOperationException("security alert status response was empty");
        Print(status, json);
        break;
    }
    case "run-security-alert-eval":
    {
        var route = "v1/ops/security/alerts/evaluate";
        route = Append(route, "force", options.Force ? "true" : null);
        var response = await http.PostAsync(route, content: null);
        await EnsureAndPrintAsync(response, json);
        break;
    }
    case "list-action-acks":
    {
        var route = "v1/ops/enforcement/acks";
        route = Append(route, "matchSessionId", options.MatchSessionId);
        route = Append(route, "accountId", options.AccountId);
        route = Append(route, "actionId", options.ActionId);
        route = Append(route, "limit", options.Limit?.ToString());
        var rows = await http.GetFromJsonAsync<List<EnforcementActionAckRecord>>(route, json)
            ?? new List<EnforcementActionAckRecord>();
        Print(rows, json);
        break;
    }
    case "create-queue-restriction":
    {
        var request = new CreateQueueRestrictionRequest(
            AccountId: options.Require("--account", options.AccountId),
            ReasonCode: options.ReasonCode ?? "manual_queue_restrict",
            DurationSeconds: options.DurationSeconds ?? 1800,
            CreatedBy: options.RequestedBy ?? "reviewer_console");
        var response = await http.PostAsJsonAsync("v1/ops/queue-restrictions", request, json);
        await EnsureAndPrintAsync(response, json);
        break;
    }
    case "list-queue-restrictions":
    {
        var route = "v1/ops/queue-restrictions";
        route = Append(route, "accountId", options.AccountId);
        route = Append(route, "status", options.Status);
        var rows = await http.GetFromJsonAsync<List<QueueRestrictionRecord>>(route, json)
            ?? new List<QueueRestrictionRecord>();
        Print(rows, json);
        break;
    }
    case "list-evidence":
    {
        var route = "v1/evidence";
        route = Append(route, "matchSessionId", options.MatchSessionId);
        route = Append(route, "accountId", options.AccountId);
        var items = await http.GetFromJsonAsync<List<EvidencePackSummary>>(route, json)
            ?? new List<EvidencePackSummary>();
        Print(items, json);
        break;
    }
    case "create-case":
    {
        var request = new CreateReviewCaseRequest(
            EvidenceId: options.Require("--evidence", options.EvidenceId),
            MatchSessionId: options.Require("--match", options.MatchSessionId),
            AccountId: options.Require("--account", options.AccountId),
            ReasonCode: options.ReasonCode ?? "manual_review",
            Priority: options.Priority ?? "normal",
            RequestedBy: options.RequestedBy ?? "reviewer_console");
        var response = await http.PostAsJsonAsync("v1/review/cases", request, json);
        await EnsureAndPrintAsync(response, json);
        break;
    }
    case "list-cases":
    {
        var route = "v1/review/cases";
        route = Append(route, "status", options.Status);
        route = Append(route, "matchSessionId", options.MatchSessionId);
        route = Append(route, "accountId", options.AccountId);
        var items = await http.GetFromJsonAsync<List<ReviewCaseSummary>>(route, json)
            ?? new List<ReviewCaseSummary>();
        Print(items, json);
        break;
    }
    case "update-case":
    {
        var request = new UpdateReviewCaseRequest(
            CaseId: options.Require("--case", options.CaseId),
            Status: options.Status ?? "in_review",
            ReviewerId: options.ReviewerId ?? "reviewer_console",
            Notes: options.Notes ?? "status updated");
        var response = await http.PostAsJsonAsync("v1/review/cases/update", request, json);
        await EnsureAndPrintAsync(response, json);
        break;
    }
    case "create-ban":
    {
        var start = DateTimeOffset.UtcNow;
        DateTimeOffset? end = null;
        if (options.DurationHours is > 0)
        {
            end = start.AddHours(options.DurationHours.Value);
        }

        var request = new CreateBanRequest(
            AccountId: options.Require("--account", options.AccountId),
            Scope: options.Scope ?? "queue",
            StartAtUtc: start,
            EndAtUtc: end,
            Reason: options.ReasonCode ?? "manual_ban",
            EvidenceId: options.EvidenceId,
            CreatedBy: options.RequestedBy ?? "reviewer_console");
        var response = await http.PostAsJsonAsync("v1/moderation/bans", request, json);
        await EnsureAndPrintAsync(response, json);
        break;
    }
    case "list-bans":
    {
        var route = "v1/moderation/bans";
        route = Append(route, "accountId", options.AccountId);
        route = Append(route, "status", options.Status);
        var items = await http.GetFromJsonAsync<List<BanRecord>>(route, json)
            ?? new List<BanRecord>();
        Print(items, json);
        break;
    }
    case "get-ban":
    {
        var ban = await http.GetFromJsonAsync<BanRecord>(
            $"v1/moderation/bans/{Uri.EscapeDataString(options.Require("--ban", options.BanId))}",
            json);
        if (ban is null)
        {
            throw new InvalidOperationException("Ban not found.");
        }

        Print(ban, json);
        break;
    }
    case "update-ban":
    {
        var status = options.Status ?? "revoked";
        DateTimeOffset? endAt = null;
        if (status is "revoked" or "expired")
        {
            endAt = DateTimeOffset.UtcNow;
        }

        var request = new UpdateBanStatusRequest(
            BanId: options.Require("--ban", options.BanId),
            Status: status,
            EndAtUtc: endAt,
            UpdatedBy: options.RequestedBy ?? "reviewer_console",
            Notes: options.Notes ?? "ban status updated");
        var response = await http.PostAsJsonAsync("v1/moderation/bans/status", request, json);
        await EnsureAndPrintAsync(response, json);
        break;
    }
    case "create-appeal":
    {
        var request = new CreateAppealRequest(
            BanId: options.Require("--ban", options.BanId),
            AccountId: options.Require("--account", options.AccountId),
            Notes: options.Notes ?? "appeal submitted");
        var response = await http.PostAsJsonAsync("v1/moderation/appeals", request, json);
        await EnsureAndPrintAsync(response, json);
        break;
    }
    case "list-appeals":
    {
        var route = "v1/moderation/appeals";
        route = Append(route, "status", options.Status);
        var items = await http.GetFromJsonAsync<List<AppealRecord>>(route, json)
            ?? new List<AppealRecord>();
        Print(items, json);
        break;
    }
    case "resolve-appeal":
    {
        var request = new ResolveAppealRequest(
            AppealId: options.Require("--appeal", options.AppealId),
            ReviewerId: options.ReviewerId ?? "reviewer_console",
            Status: options.Status ?? "upheld",
            DecisionNotes: options.Notes ?? "review complete");
        var response = await http.PostAsJsonAsync("v1/moderation/appeals/resolve", request, json);
        await EnsureAndPrintAsync(response, json);
        break;
    }
    default:
        PrintUsage();
        break;
}

static async Task EnsureAndPrintAsync(HttpResponseMessage response, JsonSerializerOptions json)
{
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"Request failed {(int)response.StatusCode}: {body}");
    }

    if (string.IsNullOrWhiteSpace(body))
    {
        Console.WriteLine("{}");
        return;
    }

    using var doc = JsonDocument.Parse(body);
    Console.WriteLine(JsonSerializer.Serialize(doc, json));
}

static string Append(string route, string key, string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return route;
    }

    var separator = route.Contains('?') ? "&" : "?";
    return $"{route}{separator}{key}={Uri.EscapeDataString(value)}";
}

static void Print<T>(T value, JsonSerializerOptions json)
{
    Console.WriteLine(JsonSerializer.Serialize(value, json));
}

static void PrintUsage()
{
    Console.WriteLine("""
    Reviewer.Console usage:
      --backend <url> --internal-api-key <key> <command> [options]

    Commands:
      system-metrics
      run-cleanup
      cleanup-status
      revoke-sessions --account <id> [--reason <code>] [--by <actor>]
      list-security-events [--since-minutes <n>] [--severity low|medium|high] [--event <type>] [--limit <n>]
      security-summary [--since-minutes <n>]
      security-alert-status
      run-security-alert-eval [--force]
      list-action-acks [--match <id>] [--account <id>] [--action <id>] [--limit <n>]
      create-queue-restriction --account <id> [--reason <code>] [--duration-sec <n>] [--by <actor>]
      list-queue-restrictions [--account <id>] [--status active|expired]
      list-evidence [--match <id>] [--account <id>]
      create-case --evidence <id> --match <id> --account <id> [--reason <code>] [--priority <level>] [--by <actor>]
      list-cases [--status <status>] [--match <id>] [--account <id>]
      update-case --case <id> [--status <status>] [--reviewer <id>] [--notes <text>]
      create-ban --account <id> [--scope queue|global] [--reason <code>] [--evidence <id>] [--duration-hours <n>] [--by <actor>]
      list-bans [--account <id>] [--status active|revoked|expired]
      get-ban --ban <id>
      update-ban --ban <id> --status active|revoked|expired [--notes <text>] [--by <actor>]
      create-appeal --ban <id> --account <id> [--notes <text>]
      list-appeals [--status <status>]
      resolve-appeal --appeal <id> [--status upheld|overturned|reduced] [--reviewer <id>] [--notes <text>]
    """);
}

internal sealed class CliOptions
{
    public string BackendBaseUrl { get; private set; } = "http://localhost:5042";
    public string InternalApiKey { get; private set; } =
        Environment.GetEnvironmentVariable("CS2IG_INTERNAL_API_KEY") ?? "dev-internal-api-key";
    public string Command { get; private set; } = string.Empty;
    public string? MatchSessionId { get; private set; }
    public string? AccountId { get; private set; }
    public string? EvidenceId { get; private set; }
    public string? CaseId { get; private set; }
    public string? BanId { get; private set; }
    public string? AppealId { get; private set; }
    public string? Status { get; private set; }
    public string? Scope { get; private set; }
    public string? ReasonCode { get; private set; }
    public string? RequestedBy { get; private set; }
    public string? ReviewerId { get; private set; }
    public string? Notes { get; private set; }
    public int? DurationHours { get; private set; }
    public string? Priority { get; private set; }
    public int? SinceMinutes { get; private set; }
    public int? Limit { get; private set; }
    public string? EventType { get; private set; }
    public string? Severity { get; private set; }
    public bool Force { get; private set; }
    public string? ActionId { get; private set; }
    public int? DurationSeconds { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                switch (arg)
                {
                    case "--backend":
                        options.BackendBaseUrl = Read(args, ++i, arg);
                        break;
                    case "--internal-api-key":
                        options.InternalApiKey = Read(args, ++i, arg);
                        break;
                    case "--match":
                        options.MatchSessionId = Read(args, ++i, arg);
                        break;
                    case "--account":
                        options.AccountId = Read(args, ++i, arg);
                        break;
                    case "--evidence":
                        options.EvidenceId = Read(args, ++i, arg);
                        break;
                    case "--case":
                        options.CaseId = Read(args, ++i, arg);
                        break;
                    case "--ban":
                        options.BanId = Read(args, ++i, arg);
                        break;
                    case "--appeal":
                        options.AppealId = Read(args, ++i, arg);
                        break;
                    case "--status":
                        options.Status = Read(args, ++i, arg);
                        break;
                    case "--scope":
                        options.Scope = Read(args, ++i, arg);
                        break;
                    case "--reason":
                        options.ReasonCode = Read(args, ++i, arg);
                        break;
                    case "--by":
                        options.RequestedBy = Read(args, ++i, arg);
                        break;
                    case "--reviewer":
                        options.ReviewerId = Read(args, ++i, arg);
                        break;
                    case "--notes":
                        options.Notes = Read(args, ++i, arg);
                        break;
                    case "--duration-hours":
                        options.DurationHours = int.Parse(Read(args, ++i, arg));
                        break;
                    case "--duration-sec":
                    case "--duration-seconds":
                        options.DurationSeconds = int.Parse(Read(args, ++i, arg));
                        break;
                    case "--priority":
                        options.Priority = Read(args, ++i, arg);
                        break;
                    case "--since-minutes":
                        options.SinceMinutes = int.Parse(Read(args, ++i, arg));
                        break;
                    case "--limit":
                        options.Limit = int.Parse(Read(args, ++i, arg));
                        break;
                    case "--event":
                    case "--event-type":
                        options.EventType = Read(args, ++i, arg);
                        break;
                    case "--action":
                    case "--action-id":
                        options.ActionId = Read(args, ++i, arg);
                        break;
                    case "--severity":
                        options.Severity = Read(args, ++i, arg);
                        break;
                    case "--force":
                        options.Force = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option {arg}");
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(options.Command))
            {
                options.Command = arg.Trim().ToLowerInvariant();
                continue;
            }

            throw new ArgumentException($"Unexpected argument: {arg}");
        }

        if (string.IsNullOrWhiteSpace(options.Command))
        {
            options.Command = "help";
        }

        return options;
    }

    public string Require(string flag, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing required argument {flag}");
        }

        return value;
    }

    private static string Read(string[] args, int index, string flag)
    {
        if (index >= args.Length)
        {
            throw new ArgumentException($"Missing value for {flag}");
        }

        return args[index];
    }
}

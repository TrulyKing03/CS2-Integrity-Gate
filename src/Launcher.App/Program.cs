using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shared.Contracts;

var options = LauncherOptions.Parse(args);
if (options.ShowUsage)
{
    PrintUsage();
    return;
}

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

var runtimeDir = Path.GetFullPath(options.RuntimeDir);
Directory.CreateDirectory(runtimeDir);
var sessionFile = Path.Combine(runtimeDir, "session.json");
var sessionSignatureFile = Path.Combine(runtimeDir, "session.sig");
var joinTokenFile = Path.Combine(runtimeDir, "join-token.json");

using var http = new HttpClient
{
    BaseAddress = new Uri(options.BackendBaseUrl.TrimEnd('/') + "/")
};

try
{
    switch (options.Command)
    {
        case "play":
            await RunPlayAsync(http, options, sessionFile, sessionSignatureFile, joinTokenFile, jsonOptions);
            break;
        case "doctor":
            await RunDoctorAsync(http, options, sessionFile, sessionSignatureFile, joinTokenFile, jsonOptions);
            break;
        case "status":
            RunStatus(options, sessionFile, sessionSignatureFile, joinTokenFile, jsonOptions);
            break;
        case "clear-runtime":
            ClearRuntime(sessionFile, sessionSignatureFile, joinTokenFile);
            break;
        default:
            throw new ArgumentException(
                $"Unknown command: {options.Command}. Expected play|doctor|status|clear-runtime|help");
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Launcher failed: {ex.Message}");
    Environment.ExitCode = 1;
}

static async Task RunPlayAsync(
    HttpClient http,
    LauncherOptions options,
    string sessionFile,
    string sessionSignatureFile,
    string joinTokenFile,
    JsonSerializerOptions jsonOptions)
{
    var identity = await ResolveIdentityAsync(http, options, jsonOptions);
    var queueRequest = new QueueRequest(identity.AccountId, identity.SteamId, options.QueueType);
    var queueResponseHttp = await SendQueueRequestAsync(http, queueRequest, identity.AccessToken, jsonOptions);
    var queue = await ReadSuccessOrThrowAsync<QueueResponse>(queueResponseHttp, jsonOptions, "queue");

    var session = new QueueSessionState(
        identity.AccountId,
        identity.SteamId,
        queue.MatchSessionId,
        queue.ServerId,
        queue.QueueType,
        DateTimeOffset.UtcNow);
    var sessionJson = JsonSerializer.Serialize(session, jsonOptions);
    await File.WriteAllTextAsync(sessionFile, sessionJson);
    await WriteSessionSignatureIfConfiguredAsync(options, sessionJson, sessionSignatureFile, jsonOptions);
    if (File.Exists(joinTokenFile))
    {
        File.Delete(joinTokenFile);
    }

    Console.WriteLine($"Match allocated: session={queue.MatchSessionId}, server={queue.ServerId}, queue={queue.QueueType}");
    Console.WriteLine($"Waiting for AC join token at {joinTokenFile}...");

    var joinTokenData = await WaitForJoinTokenAsync(
        joinTokenFile,
        queue.MatchSessionId,
        identity.AccountId,
        timeout: TimeSpan.FromSeconds(options.TokenWaitSec),
        jsonOptions);
    if (joinTokenData is null)
    {
        throw new TimeoutException($"AC did not produce a join token within {options.TokenWaitSec} seconds.");
    }

    if (options.SelfValidateJoin)
    {
        http.DefaultRequestHeaders.Remove("X-Server-Api-Key");
        http.DefaultRequestHeaders.Add("X-Server-Api-Key", options.ServerApiKey);
        var validateRequest = new ValidateJoinRequest(
            queue.ServerId,
            identity.SteamId,
            identity.AccountId,
            queue.MatchSessionId,
            joinTokenData.JoinToken);
        var validateResponseHttp = await http.PostAsJsonAsync("v1/attestation/validate-join", validateRequest, jsonOptions);
        var validate = await ReadSuccessOrThrowAsync<ValidateJoinResponse>(validateResponseHttp, jsonOptions, "validate-join");

        if (!validate.Allow)
        {
            throw new InvalidOperationException($"Join rejected: {validate.Reason}");
        }

        Console.WriteLine("Self validation succeeded.");
    }

    var cs2Args = $"+connect {queue.ServerId} +setinfo ac_join_token \"{joinTokenData.JoinToken}\"";
    Console.WriteLine("Join token acquired. Server must validate token on connect.");
    Console.WriteLine($"Launch command: {options.Cs2Path} {cs2Args}");

    if (!options.DryRun)
    {
        if (!File.Exists(options.Cs2Path))
        {
            throw new FileNotFoundException("CS2 executable was not found.", options.Cs2Path);
        }

        _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = options.Cs2Path,
            Arguments = cs2Args,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Failed to launch CS2 process.");
    }

    if (!options.KeepRuntimeFiles)
    {
        DeleteFileIfExists(sessionFile);
        DeleteFileIfExists(sessionSignatureFile);
        DeleteFileIfExists(joinTokenFile);
    }

    Console.WriteLine("Launcher flow complete.");
}

static async Task RunDoctorAsync(
    HttpClient http,
    LauncherOptions options,
    string sessionFile,
    string sessionSignatureFile,
    string joinTokenFile,
    JsonSerializerOptions jsonOptions)
{
    var checks = new List<(string Name, bool Ok, string Details)>();

    try
    {
        var response = await http.GetAsync("healthz");
        checks.Add((
            "backend_healthz",
            response.IsSuccessStatusCode,
            response.IsSuccessStatusCode ? "ok" : $"{(int)response.StatusCode} {response.ReasonPhrase}"));
    }
    catch (Exception ex)
    {
        checks.Add(("backend_healthz", false, ex.Message));
    }

    checks.Add((
        "cs2_path",
        File.Exists(options.Cs2Path),
        options.Cs2Path));

    checks.Add((
        "runtime_dir",
        Directory.Exists(options.RuntimeDir),
        Path.GetFullPath(options.RuntimeDir)));

    if (TryReadJson<QueueSessionState>(sessionFile, jsonOptions, out var session, out var sessionError))
    {
        checks.Add((
            "runtime_session",
            true,
            $"match={session!.MatchSessionId} account={session.AccountId} created={session.CreatedAtUtc:O}"));
    }
    else
    {
        checks.Add(("runtime_session", false, sessionError));
    }

    if (!string.IsNullOrWhiteSpace(options.RuntimeSigningKey))
    {
        var signatureOk = VerifySessionSignature(sessionFile, sessionSignatureFile, options.RuntimeSigningKey, jsonOptions);
        checks.Add((
            "runtime_session_signature",
            signatureOk,
            signatureOk ? "valid" : "missing_or_invalid"));
    }
    else
    {
        checks.Add(("runtime_session_signature", true, "not_configured"));
    }

    if (TryReadJson<JoinTokenFile>(joinTokenFile, jsonOptions, out var token, out var tokenError))
    {
        var freshness = token!.ExpiresAtUtc > DateTimeOffset.UtcNow ? "fresh" : "expired";
        checks.Add((
            "runtime_join_token",
            token.ExpiresAtUtc > DateTimeOffset.UtcNow,
            $"match={token.MatchSessionId} expires={token.ExpiresAtUtc:O} ({freshness})"));
    }
    else
    {
        checks.Add(("runtime_join_token", false, tokenError));
    }

    var failed = checks.Count(check => !check.Ok);
    foreach (var check in checks)
    {
        var prefix = check.Ok ? "[OK]" : "[FAIL]";
        Console.WriteLine($"{prefix} {check.Name}: {check.Details}");
    }

    Console.WriteLine($"Doctor summary: total={checks.Count}, failed={failed}");
    if (failed > 0)
    {
        Environment.ExitCode = 1;
    }
}

static void RunStatus(
    LauncherOptions options,
    string sessionFile,
    string sessionSignatureFile,
    string joinTokenFile,
    JsonSerializerOptions jsonOptions)
{
    Console.WriteLine($"Launcher command: {options.Command}");
    Console.WriteLine($"Backend: {options.BackendBaseUrl}");
    Console.WriteLine($"Runtime: {Path.GetFullPath(options.RuntimeDir)}");
    Console.WriteLine($"Queue: {options.QueueType}");

    if (TryReadJson<QueueSessionState>(sessionFile, jsonOptions, out var session, out _))
    {
        Console.WriteLine(
            $"Session: match={session!.MatchSessionId} account={session.AccountId} server={session.ServerId} created={session.CreatedAtUtc:O}");
    }
    else
    {
        Console.WriteLine("Session: missing");
    }

    if (!string.IsNullOrWhiteSpace(options.RuntimeSigningKey))
    {
        var signatureOk = VerifySessionSignature(sessionFile, sessionSignatureFile, options.RuntimeSigningKey, jsonOptions);
        Console.WriteLine($"SessionSignature: {(signatureOk ? "valid" : "missing_or_invalid")}");
    }
    else
    {
        Console.WriteLine("SessionSignature: not_configured");
    }

    if (TryReadJson<JoinTokenFile>(joinTokenFile, jsonOptions, out var token, out _))
    {
        var state = token!.ExpiresAtUtc > DateTimeOffset.UtcNow ? "fresh" : "expired";
        Console.WriteLine($"JoinToken: match={token.MatchSessionId} expires={token.ExpiresAtUtc:O} ({state})");
    }
    else
    {
        Console.WriteLine("JoinToken: missing");
    }
}

static void ClearRuntime(string sessionFile, string sessionSignatureFile, string joinTokenFile)
{
    DeleteFileIfExists(sessionFile);
    DeleteFileIfExists(sessionSignatureFile);
    DeleteFileIfExists(joinTokenFile);

    Console.WriteLine("Runtime files cleared.");
}

static async Task WriteSessionSignatureIfConfiguredAsync(
    LauncherOptions options,
    string sessionJson,
    string sessionSignatureFile,
    JsonSerializerOptions jsonOptions)
{
    if (string.IsNullOrWhiteSpace(options.RuntimeSigningKey))
    {
        DeleteFileIfExists(sessionSignatureFile);
        return;
    }

    var signature = ComputeSessionSignature(options.RuntimeSigningKey, sessionJson);
    var payload = new RuntimeSignatureFile("hmac-sha256", signature, DateTimeOffset.UtcNow);
    await File.WriteAllTextAsync(sessionSignatureFile, JsonSerializer.Serialize(payload, jsonOptions));
}

static bool VerifySessionSignature(
    string sessionFile,
    string sessionSignatureFile,
    string runtimeSigningKey,
    JsonSerializerOptions jsonOptions)
{
    if (string.IsNullOrWhiteSpace(runtimeSigningKey))
    {
        return true;
    }

    if (!File.Exists(sessionFile) || !File.Exists(sessionSignatureFile))
    {
        return false;
    }

    try
    {
        var sessionJson = File.ReadAllText(sessionFile);
        var signatureJson = File.ReadAllText(sessionSignatureFile);
        var signatureFile = JsonSerializer.Deserialize<RuntimeSignatureFile>(signatureJson, jsonOptions);
        if (signatureFile is null ||
            !string.Equals(signatureFile.Algorithm, "hmac-sha256", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(signatureFile.Signature))
        {
            return false;
        }

        var expected = ComputeSessionSignature(runtimeSigningKey, sessionJson);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(signatureFile.Signature.Trim());
        return expectedBytes.Length == providedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
    catch
    {
        return false;
    }
}

static string ComputeSessionSignature(string runtimeSigningKey, string sessionJson)
{
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(runtimeSigningKey));
    var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(sessionJson));
    return Convert.ToHexString(hash).ToLowerInvariant();
}

static void DeleteFileIfExists(string path)
{
    if (File.Exists(path))
    {
        File.Delete(path);
    }
}

static async Task<ResolvedIdentity> ResolveIdentityAsync(
    HttpClient http,
    LauncherOptions options,
    JsonSerializerOptions jsonOptions)
{
    var accountId = options.AccountId;
    var steamId = options.SteamId;
    if (!string.IsNullOrWhiteSpace(accountId) && !string.IsNullOrWhiteSpace(steamId))
    {
        return new ResolvedIdentity(accountId, steamId, options.AccessToken);
    }

    var loginRequest = new LoginRequest(
        options.Username ?? "local_player",
        options.Password ?? "local_password");
    var loginResponse = await http.PostAsJsonAsync("v1/auth/login", loginRequest, jsonOptions);
    var login = await ReadSuccessOrThrowAsync<LoginResponse>(loginResponse, jsonOptions, "login");
    var effectiveAccessToken = string.IsNullOrWhiteSpace(options.AccessToken) ? login.AccessToken : options.AccessToken;
    if (!string.IsNullOrWhiteSpace(options.SteamId))
    {
        return new ResolvedIdentity(login.AccountId, options.SteamId, effectiveAccessToken);
    }

    return new ResolvedIdentity(login.AccountId, login.SteamId, effectiveAccessToken);
}

static Task<HttpResponseMessage> SendQueueRequestAsync(
    HttpClient http,
    QueueRequest request,
    string? accessToken,
    JsonSerializerOptions jsonOptions)
{
    if (string.IsNullOrWhiteSpace(accessToken))
    {
        return http.PostAsJsonAsync("v1/queue/enqueue", request, jsonOptions);
    }

    var message = new HttpRequestMessage(HttpMethod.Post, "v1/queue/enqueue")
    {
        Content = JsonContent.Create(request, options: jsonOptions)
    };
    message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
    return http.SendAsync(message);
}

static bool TryReadJson<T>(
    string path,
    JsonSerializerOptions jsonOptions,
    out T? value,
    out string error)
{
    value = default;
    error = string.Empty;
    if (!File.Exists(path))
    {
        error = "missing";
        return false;
    }

    try
    {
        var content = File.ReadAllText(path);
        value = JsonSerializer.Deserialize<T>(content, jsonOptions);
        if (value is null)
        {
            error = "parse_failed";
            return false;
        }

        return true;
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return false;
    }
}

static async Task<JoinTokenFile?> WaitForJoinTokenAsync(
    string path,
    string expectedMatchSessionId,
    string expectedAccountId,
    TimeSpan timeout,
    JsonSerializerOptions jsonOptions)
{
    var started = DateTimeOffset.UtcNow;
    while (DateTimeOffset.UtcNow - started < timeout)
    {
        if (File.Exists(path))
        {
            var json = await File.ReadAllTextAsync(path);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var token = JsonSerializer.Deserialize<JoinTokenFile>(json, jsonOptions);
                if (token is not null &&
                    !string.IsNullOrWhiteSpace(token.JoinToken) &&
                    string.Equals(token.MatchSessionId, expectedMatchSessionId, StringComparison.Ordinal) &&
                    string.Equals(token.AccountId, expectedAccountId, StringComparison.Ordinal) &&
                    token.ExpiresAtUtc > DateTimeOffset.UtcNow)
                {
                    return token;
                }
            }
        }

        await Task.Delay(1000);
    }

    return null;
}

static async Task<T> ReadSuccessOrThrowAsync<T>(
    HttpResponseMessage response,
    JsonSerializerOptions jsonOptions,
    string operation)
{
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        var error = TryExtractApiError(body);
        throw new InvalidOperationException(
            $"{operation} failed with {(int)response.StatusCode} ({response.ReasonPhrase}). {error}");
    }

    if (string.IsNullOrWhiteSpace(body))
    {
        throw new InvalidOperationException($"{operation} response was empty.");
    }

    var parsed = JsonSerializer.Deserialize<T>(body, jsonOptions);
    return parsed ?? throw new InvalidOperationException($"{operation} response parse failed.");
}

static string TryExtractApiError(string body)
{
    if (string.IsNullOrWhiteSpace(body))
    {
        return "No error payload was returned by backend.";
    }

    try
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("error", out var errorNode) &&
            errorNode.ValueKind == JsonValueKind.String)
        {
            return $"error={errorNode.GetString()}";
        }
    }
    catch (JsonException)
    {
    }

    return $"body={body}";
}

static void PrintUsage()
{
    Console.WriteLine("""
    Launcher.App usage:
      dotnet run --project src/Launcher.App -- [command] [options]

    Commands:
      play          Queue + wait for join token + print launch command (default)
      doctor        Validate backend/cs2/runtime/session/token status
      status        Show launcher and runtime status
      clear-runtime Delete runtime/session.json and runtime/join-token.json
      help          Show this help

    Common options:
      --profile <json>
      --backend <url>
      --runtime <dir>
      --account <id>
      --steam <id>
      --access-token <token>
      --queue <high_trust|standard>
      --token-wait-sec <n>
      --cs2-path <path>
      --self-validate
      --server-api-key <key>
      --runtime-signing-key <key>
      --keep-runtime
      --no-dry-run
    """);
}

internal sealed record JoinTokenFile(
    string MatchSessionId,
    string AccountId,
    string SteamId,
    string ServerId,
    string JoinToken,
    DateTimeOffset ExpiresAtUtc);

internal sealed record RuntimeSignatureFile(
    string Algorithm,
    string Signature,
    DateTimeOffset CreatedAtUtc);

internal sealed record ResolvedIdentity(
    string AccountId,
    string SteamId,
    string? AccessToken);

internal sealed class LauncherOptions
{
    public string Command { get; private set; } = "play";
    public bool ShowUsage { get; private set; }
    public string? ProfilePath { get; private set; }
    public string BackendBaseUrl { get; private set; } = "http://localhost:5042";
    public string QueueType { get; private set; } = "high_trust";
    public string RuntimeDir { get; private set; } = "runtime";
    public string Cs2Path { get; private set; } = @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe";
    public string Username { get; private set; } = "player1";
    public string Password { get; private set; } = "password";
    public string AccountId { get; private set; } = string.Empty;
    public string SteamId { get; private set; } = string.Empty;
    public string AccessToken { get; private set; } = string.Empty;
    public int TokenWaitSec { get; private set; } = 90;
    public bool DryRun { get; private set; } = true;
    public bool KeepRuntimeFiles { get; private set; }
    public bool SelfValidateJoin { get; private set; }
    public string ServerApiKey { get; private set; } =
        Environment.GetEnvironmentVariable("CS2IG_SERVER_API_KEY") ?? "dev-server-api-key";
    public string RuntimeSigningKey { get; private set; } =
        Environment.GetEnvironmentVariable("CS2IG_RUNTIME_SIGNING_KEY") ?? string.Empty;

    public static LauncherOptions Parse(string[] args)
    {
        var options = new LauncherOptions();
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--profile", StringComparison.Ordinal))
            {
                continue;
            }

            options.ProfilePath = ReadValue(args, ++i, "--profile");
            var profilePath = Path.GetFullPath(options.ProfilePath);
            if (!File.Exists(profilePath))
            {
                throw new FileNotFoundException("Launcher profile file not found.", profilePath);
            }

            var profileContent = File.ReadAllText(profilePath);
            var profile = JsonSerializer.Deserialize<LauncherProfile>(profileContent, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException($"Launcher profile parse failed: {profilePath}");
            options.ApplyProfile(profile);
        }

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                options.Command = arg.Trim().ToLowerInvariant();
                continue;
            }

            switch (arg)
            {
                case "--profile":
                    i++;
                    break;
                case "--command":
                    options.Command = ReadValue(args, ++i, "--command").Trim().ToLowerInvariant();
                    break;
                case "--backend":
                    options.BackendBaseUrl = ReadValue(args, ++i, "--backend");
                    break;
                case "--queue":
                    options.QueueType = ReadValue(args, ++i, "--queue");
                    break;
                case "--runtime":
                    options.RuntimeDir = ReadValue(args, ++i, "--runtime");
                    break;
                case "--cs2-path":
                    options.Cs2Path = ReadValue(args, ++i, "--cs2-path");
                    break;
                case "--username":
                    options.Username = ReadValue(args, ++i, "--username");
                    break;
                case "--password":
                    options.Password = ReadValue(args, ++i, "--password");
                    break;
                case "--account":
                    options.AccountId = ReadValue(args, ++i, "--account");
                    break;
                case "--steam":
                    options.SteamId = ReadValue(args, ++i, "--steam");
                    break;
                case "--access-token":
                    options.AccessToken = ReadValue(args, ++i, "--access-token");
                    break;
                case "--token-wait-sec":
                    options.TokenWaitSec = int.Parse(ReadValue(args, ++i, "--token-wait-sec"));
                    break;
                case "--no-dry-run":
                    options.DryRun = false;
                    break;
                case "--keep-runtime":
                    options.KeepRuntimeFiles = true;
                    break;
                case "--self-validate":
                    options.SelfValidateJoin = true;
                    break;
                case "--server-api-key":
                    options.ServerApiKey = ReadValue(args, ++i, "--server-api-key");
                    break;
                case "--runtime-signing-key":
                    options.RuntimeSigningKey = ReadValue(args, ++i, "--runtime-signing-key");
                    break;
                case "--help":
                case "-h":
                    options.ShowUsage = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option {arg}");
            }
        }

        if (options.Command is "help")
        {
            options.ShowUsage = true;
        }

        return options;
    }

    private void ApplyProfile(LauncherProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.Command))
        {
            Command = profile.Command.Trim().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(profile.BackendBaseUrl))
        {
            BackendBaseUrl = profile.BackendBaseUrl;
        }

        if (!string.IsNullOrWhiteSpace(profile.QueueType))
        {
            QueueType = profile.QueueType;
        }

        if (!string.IsNullOrWhiteSpace(profile.RuntimeDir))
        {
            RuntimeDir = profile.RuntimeDir;
        }

        if (!string.IsNullOrWhiteSpace(profile.Cs2Path))
        {
            Cs2Path = profile.Cs2Path;
        }

        if (!string.IsNullOrWhiteSpace(profile.Username))
        {
            Username = profile.Username;
        }

        if (!string.IsNullOrWhiteSpace(profile.Password))
        {
            Password = profile.Password;
        }

        if (!string.IsNullOrWhiteSpace(profile.AccountId))
        {
            AccountId = profile.AccountId;
        }

        if (!string.IsNullOrWhiteSpace(profile.SteamId))
        {
            SteamId = profile.SteamId;
        }

        if (!string.IsNullOrWhiteSpace(profile.AccessToken))
        {
            AccessToken = profile.AccessToken;
        }

        if (profile.TokenWaitSec is > 0)
        {
            TokenWaitSec = profile.TokenWaitSec.Value;
        }

        if (profile.DryRun is not null)
        {
            DryRun = profile.DryRun.Value;
        }

        if (profile.KeepRuntimeFiles is not null)
        {
            KeepRuntimeFiles = profile.KeepRuntimeFiles.Value;
        }

        if (profile.SelfValidateJoin is not null)
        {
            SelfValidateJoin = profile.SelfValidateJoin.Value;
        }

        if (!string.IsNullOrWhiteSpace(profile.ServerApiKey))
        {
            ServerApiKey = profile.ServerApiKey;
        }

        if (!string.IsNullOrWhiteSpace(profile.RuntimeSigningKey))
        {
            RuntimeSigningKey = profile.RuntimeSigningKey;
        }
    }

    private static string ReadValue(string[] args, int index, string key)
    {
        if (index >= args.Length)
        {
            throw new ArgumentException($"Missing value for {key}");
        }

        return args[index];
    }
}

internal sealed class LauncherProfile
{
    public string? Command { get; set; }
    public string? BackendBaseUrl { get; set; }
    public string? QueueType { get; set; }
    public string? RuntimeDir { get; set; }
    public string? Cs2Path { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? AccountId { get; set; }
    public string? SteamId { get; set; }
    public string? AccessToken { get; set; }
    public int? TokenWaitSec { get; set; }
    public bool? DryRun { get; set; }
    public bool? KeepRuntimeFiles { get; set; }
    public bool? SelfValidateJoin { get; set; }
    public string? ServerApiKey { get; set; }
    public string? RuntimeSigningKey { get; set; }
}

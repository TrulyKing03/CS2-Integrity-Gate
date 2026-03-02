using System.Net.Http.Json;
using System.Text.Json;
using Shared.Contracts;

var options = LauncherOptions.Parse(args);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

var runtimeDir = Path.GetFullPath(options.RuntimeDir);
Directory.CreateDirectory(runtimeDir);
var sessionFile = Path.Combine(runtimeDir, "session.json");
var joinTokenFile = Path.Combine(runtimeDir, "join-token.json");

using var http = new HttpClient
{
    BaseAddress = new Uri(options.BackendBaseUrl.TrimEnd('/') + "/")
};

try
{
    string accountId = options.AccountId;
    string steamId = options.SteamId;

    if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(steamId))
    {
        var loginRequest = new LoginRequest(
            options.Username ?? "local_player",
            options.Password ?? "local_password");
        var loginResponse = await http.PostAsJsonAsync("v1/auth/login", loginRequest, jsonOptions);
        var login = await ReadSuccessOrThrowAsync<LoginResponse>(loginResponse, jsonOptions, "login");
        accountId = login.AccountId;
        steamId = login.SteamId;
    }

    if (!string.IsNullOrWhiteSpace(options.SteamId))
    {
        steamId = options.SteamId;
    }

    var queueRequest = new QueueRequest(accountId, steamId, options.QueueType);
    var queueResponseHttp = await http.PostAsJsonAsync("v1/queue/enqueue", queueRequest, jsonOptions);
    var queue = await ReadSuccessOrThrowAsync<QueueResponse>(queueResponseHttp, jsonOptions, "queue");

    var session = new QueueSessionState(
        accountId,
        steamId,
        queue.MatchSessionId,
        queue.ServerId,
        queue.QueueType,
        DateTimeOffset.UtcNow);
    await File.WriteAllTextAsync(sessionFile, JsonSerializer.Serialize(session, jsonOptions));
    if (File.Exists(joinTokenFile))
    {
        File.Delete(joinTokenFile);
    }

    Console.WriteLine($"Match allocated: session={queue.MatchSessionId}, server={queue.ServerId}, queue={queue.QueueType}");
    Console.WriteLine($"Waiting for AC join token at {joinTokenFile}...");

    var joinTokenData = await WaitForJoinTokenAsync(
        joinTokenFile,
        queue.MatchSessionId,
        accountId,
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
            steamId,
            accountId,
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

    fileCleanup();
    Console.WriteLine("Launcher flow complete.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Launcher failed: {ex.Message}");
    Environment.ExitCode = 1;
}

void fileCleanup()
{
    if (!options.KeepRuntimeFiles)
    {
        if (File.Exists(sessionFile))
        {
            File.Delete(sessionFile);
        }
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

internal sealed record JoinTokenFile(
    string MatchSessionId,
    string AccountId,
    string SteamId,
    string ServerId,
    string JoinToken,
    DateTimeOffset ExpiresAtUtc);

internal sealed class LauncherOptions
{
    public string BackendBaseUrl { get; private set; } = "http://localhost:5042";
    public string QueueType { get; private set; } = "high_trust";
    public string RuntimeDir { get; private set; } = "runtime";
    public string Cs2Path { get; private set; } = @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe";
    public string Username { get; private set; } = "player1";
    public string Password { get; private set; } = "password";
    public string AccountId { get; private set; } = string.Empty;
    public string SteamId { get; private set; } = string.Empty;
    public int TokenWaitSec { get; private set; } = 90;
    public bool DryRun { get; private set; } = true;
    public bool KeepRuntimeFiles { get; private set; } = false;
    public bool SelfValidateJoin { get; private set; } = false;
    public string ServerApiKey { get; private set; } =
        Environment.GetEnvironmentVariable("CS2IG_SERVER_API_KEY") ?? "dev-server-api-key";

    public static LauncherOptions Parse(string[] args)
    {
        var options = new LauncherOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
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
            }
        }

        return options;
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

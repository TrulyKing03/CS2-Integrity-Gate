using System.Text.Json;
using ControlPlane.Api.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Shared.Contracts;

namespace ControlPlane.Api.Persistence;

public interface ISqliteStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task EnsureAccountAsync(string accountId, string steamId, CancellationToken cancellationToken);
    Task UpsertDeviceAsync(
        string deviceId,
        string accountId,
        string steamId,
        string devicePubkeyPem,
        string status,
        CancellationToken cancellationToken);
    Task CreateMatchSessionAsync(QueueResponse queueResponse, string accountId, string steamId, CancellationToken cancellationToken);
    Task<bool> IsMatchPlayerAsync(string matchSessionId, string accountId, string steamId, CancellationToken cancellationToken);
    Task<string?> GetSteamIdForMatchPlayerAsync(string matchSessionId, string accountId, CancellationToken cancellationToken);
    Task<string?> GetServerIdForMatchAsync(string matchSessionId, CancellationToken cancellationToken);
    Task SaveJoinTokenAsync(JoinTokenPayload payload, CancellationToken cancellationToken);
    Task<JoinTokenDbRecord?> GetJoinTokenAsync(string jti, CancellationToken cancellationToken);
    Task MarkJoinTokenUsedAsync(string jti, CancellationToken cancellationToken);
    Task AddHeartbeatAsync(
        HeartbeatRequest request,
        string steamId,
        string status,
        CancellationToken cancellationToken);
    Task<HeartbeatDbRecord?> GetLatestHeartbeatAsync(
        string matchSessionId,
        string accountId,
        CancellationToken cancellationToken);
    Task UpsertPlayerHealthAsync(
        string matchSessionId,
        string accountId,
        string steamId,
        string status,
        DateTimeOffset lastHeartbeatUtc,
        string recommendedAction,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<PlayerHealthState>> GetMatchHealthAsync(string matchSessionId, CancellationToken cancellationToken);
    Task AddTelemetryEventsAsync<T>(
        string matchSessionId,
        string eventType,
        IEnumerable<T> items,
        Func<T, string?> accountSelector,
        Func<T, long> tickSelector,
        CancellationToken cancellationToken);
    Task UpsertSuspicionScoreAsync(SuspicionScoreUpdate update, CancellationToken cancellationToken);
    Task<IReadOnlyList<SuspicionScoreUpdate>> GetSuspicionScoresAsync(string matchSessionId, string accountId, CancellationToken cancellationToken);
    Task AddEnforcementActionAsync(EnforcementAction action, string detailsJson, CancellationToken cancellationToken);
    Task<IReadOnlyList<EnforcementAction>> GetEnforcementActionsAsync(string matchSessionId, CancellationToken cancellationToken);
}

public sealed record JoinTokenDbRecord(string Jti, string PayloadJson, DateTimeOffset? UsedAtUtc);

public sealed record HeartbeatDbRecord(
    string MatchSessionId,
    string AccountId,
    string SteamId,
    long Sequence,
    DateTimeOffset ReceivedAtUtc,
    string Status);

public sealed class SqliteStore : ISqliteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;

    public SqliteStore(IOptions<StorageOptions> storageOptions)
    {
        var sqlitePath = storageOptions.Value.SqlitePath;
        if (string.IsNullOrWhiteSpace(sqlitePath))
        {
            throw new InvalidOperationException("Storage:SqlitePath must be configured.");
        }

        var fullPath = Path.GetFullPath(sqlitePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = $"Data Source={fullPath};Cache=Shared";
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS accounts (
                account_id TEXT PRIMARY KEY,
                created_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS steam_links (
                account_id TEXT PRIMARY KEY,
                steam_id TEXT NOT NULL UNIQUE,
                linked_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS devices (
                device_id TEXT PRIMARY KEY,
                account_id TEXT NOT NULL,
                steam_id TEXT NOT NULL,
                device_pubkey_pem TEXT NOT NULL,
                status TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS match_sessions (
                match_session_id TEXT PRIMARY KEY,
                queue_type TEXT NOT NULL,
                region TEXT NOT NULL,
                server_id TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                status TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS match_players (
                match_session_id TEXT NOT NULL,
                account_id TEXT NOT NULL,
                steam_id TEXT NOT NULL,
                team TEXT NOT NULL,
                trust_tier TEXT NOT NULL,
                joined_at_utc TEXT NOT NULL,
                left_at_utc TEXT NULL,
                PRIMARY KEY (match_session_id, account_id)
            );

            CREATE TABLE IF NOT EXISTS join_tokens (
                jti TEXT PRIMARY KEY,
                match_session_id TEXT NOT NULL,
                account_id TEXT NOT NULL,
                steam_id TEXT NOT NULL,
                server_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                issued_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                used_at_utc TEXT NULL,
                payload_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS heartbeats (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                match_session_id TEXT NOT NULL,
                account_id TEXT NOT NULL,
                steam_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                seq INTEGER NOT NULL,
                received_at_utc TEXT NOT NULL,
                status TEXT NOT NULL,
                latency_ms INTEGER NOT NULL,
                signals_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS player_health (
                match_session_id TEXT NOT NULL,
                account_id TEXT NOT NULL,
                steam_id TEXT NOT NULL,
                status TEXT NOT NULL,
                last_heartbeat_utc TEXT NOT NULL,
                recommended_action TEXT NOT NULL,
                PRIMARY KEY (match_session_id, account_id)
            );

            CREATE TABLE IF NOT EXISTS telemetry_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                match_session_id TEXT NOT NULL,
                tick_id INTEGER NOT NULL,
                event_type TEXT NOT NULL,
                account_id TEXT NULL,
                payload_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_telemetry_events_match_tick
                ON telemetry_events (match_session_id, tick_id);

            CREATE TABLE IF NOT EXISTS suspicion_scores (
                match_session_id TEXT NOT NULL,
                account_id TEXT NOT NULL,
                channel TEXT NOT NULL,
                score REAL NOT NULL,
                confidence REAL NOT NULL,
                sample_size INTEGER NOT NULL,
                updated_at_utc TEXT NOT NULL,
                PRIMARY KEY (match_session_id, account_id, channel)
            );

            CREATE TABLE IF NOT EXISTS enforcement_actions (
                action_id TEXT PRIMARY KEY,
                match_session_id TEXT NOT NULL,
                account_id TEXT NOT NULL,
                action_type TEXT NOT NULL,
                reason_code TEXT NOT NULL,
                duration_seconds INTEGER NOT NULL,
                created_at_utc TEXT NOT NULL,
                details_json TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EnsureAccountAsync(string accountId, string steamId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO accounts (account_id, created_at_utc)
                VALUES ($account_id, $created_at)
                ON CONFLICT(account_id) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$account_id", accountId);
            cmd.Parameters.AddWithValue("$created_at", now.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO steam_links (account_id, steam_id, linked_at_utc)
                VALUES ($account_id, $steam_id, $linked_at)
                ON CONFLICT(account_id) DO UPDATE SET
                    steam_id = excluded.steam_id,
                    linked_at_utc = excluded.linked_at_utc;
                """;
            cmd.Parameters.AddWithValue("$account_id", accountId);
            cmd.Parameters.AddWithValue("$steam_id", steamId);
            cmd.Parameters.AddWithValue("$linked_at", now.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task UpsertDeviceAsync(
        string deviceId,
        string accountId,
        string steamId,
        string devicePubkeyPem,
        string status,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO devices (device_id, account_id, steam_id, device_pubkey_pem, status, last_seen_utc)
            VALUES ($device_id, $account_id, $steam_id, $device_pubkey_pem, $status, $last_seen)
            ON CONFLICT(device_id) DO UPDATE SET
                account_id = excluded.account_id,
                steam_id = excluded.steam_id,
                device_pubkey_pem = excluded.device_pubkey_pem,
                status = excluded.status,
                last_seen_utc = excluded.last_seen_utc;
            """;
        cmd.Parameters.AddWithValue("$device_id", deviceId);
        cmd.Parameters.AddWithValue("$account_id", accountId);
        cmd.Parameters.AddWithValue("$steam_id", steamId);
        cmd.Parameters.AddWithValue("$device_pubkey_pem", devicePubkeyPem);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$last_seen", now.ToString("O"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CreateMatchSessionAsync(
        QueueResponse queueResponse,
        string accountId,
        string steamId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO match_sessions (match_session_id, queue_type, region, server_id, created_at_utc, status)
                VALUES ($match_session_id, $queue_type, $region, $server_id, $created_at, $status)
                ON CONFLICT(match_session_id) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$match_session_id", queueResponse.MatchSessionId);
            cmd.Parameters.AddWithValue("$queue_type", queueResponse.QueueType);
            cmd.Parameters.AddWithValue("$region", queueResponse.Region);
            cmd.Parameters.AddWithValue("$server_id", queueResponse.ServerId);
            cmd.Parameters.AddWithValue("$created_at", queueResponse.EstimatedStartUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$status", "created");
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO match_players (match_session_id, account_id, steam_id, team, trust_tier, joined_at_utc, left_at_utc)
                VALUES ($match_session_id, $account_id, $steam_id, $team, $trust_tier, $joined_at, NULL)
                ON CONFLICT(match_session_id, account_id) DO UPDATE SET
                    steam_id = excluded.steam_id,
                    trust_tier = excluded.trust_tier;
                """;
            cmd.Parameters.AddWithValue("$match_session_id", queueResponse.MatchSessionId);
            cmd.Parameters.AddWithValue("$account_id", accountId);
            cmd.Parameters.AddWithValue("$steam_id", steamId);
            cmd.Parameters.AddWithValue("$team", "UNASSIGNED");
            cmd.Parameters.AddWithValue("$trust_tier", queueResponse.QueueType == "high_trust" ? "high_trust" : "standard");
            cmd.Parameters.AddWithValue("$joined_at", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<bool> IsMatchPlayerAsync(string matchSessionId, string accountId, string steamId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1)
            FROM match_players
            WHERE match_session_id = $match_session_id
              AND account_id = $account_id
              AND steam_id = $steam_id;
            """;
        cmd.Parameters.AddWithValue("$match_session_id", matchSessionId);
        cmd.Parameters.AddWithValue("$account_id", accountId);
        cmd.Parameters.AddWithValue("$steam_id", steamId);
        var count = (long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0L);
        return count > 0;
    }

    public async Task<string?> GetSteamIdForMatchPlayerAsync(string matchSessionId, string accountId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT steam_id
            FROM match_players
            WHERE match_session_id = $match_session_id
              AND account_id = $account_id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$match_session_id", matchSessionId);
        cmd.Parameters.AddWithValue("$account_id", accountId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }

    public async Task<string?> GetServerIdForMatchAsync(string matchSessionId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT server_id
            FROM match_sessions
            WHERE match_session_id = $match_session_id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$match_session_id", matchSessionId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }

    public async Task SaveJoinTokenAsync(JoinTokenPayload payload, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO join_tokens (
                jti, match_session_id, account_id, steam_id, server_id, device_id,
                issued_at_utc, expires_at_utc, used_at_utc, payload_json
            )
            VALUES (
                $jti, $match_session_id, $account_id, $steam_id, $server_id, $device_id,
                $issued_at, $expires_at, NULL, $payload_json
            )
            ON CONFLICT(jti) DO UPDATE SET
                payload_json = excluded.payload_json,
                expires_at_utc = excluded.expires_at_utc;
            """;
        cmd.Parameters.AddWithValue("$jti", payload.Jti);
        cmd.Parameters.AddWithValue("$match_session_id", payload.MatchSessionId);
        cmd.Parameters.AddWithValue("$account_id", payload.AccountId);
        cmd.Parameters.AddWithValue("$steam_id", payload.SteamId);
        cmd.Parameters.AddWithValue("$server_id", payload.ServerId);
        cmd.Parameters.AddWithValue("$device_id", payload.DeviceId);
        cmd.Parameters.AddWithValue("$issued_at", payload.IssuedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$expires_at", payload.ExpiresAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(payload, JsonOptions));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<JoinTokenDbRecord?> GetJoinTokenAsync(string jti, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT jti, payload_json, used_at_utc
            FROM join_tokens
            WHERE jti = $jti;
            """;
        cmd.Parameters.AddWithValue("$jti", jti);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var usedAtText = reader.IsDBNull(2) ? null : reader.GetString(2);
        DateTimeOffset? usedAt = null;
        if (!string.IsNullOrWhiteSpace(usedAtText) && DateTimeOffset.TryParse(usedAtText, out var parsed))
        {
            usedAt = parsed;
        }

        return new JoinTokenDbRecord(
            reader.GetString(0),
            reader.GetString(1),
            usedAt);
    }

    public async Task MarkJoinTokenUsedAsync(string jti, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE join_tokens
            SET used_at_utc = $used_at
            WHERE jti = $jti;
            """;
        cmd.Parameters.AddWithValue("$used_at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$jti", jti);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddHeartbeatAsync(
        HeartbeatRequest request,
        string steamId,
        string status,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO heartbeats (
                match_session_id, account_id, steam_id, device_id, seq, received_at_utc, status, latency_ms, signals_json
            )
            VALUES (
                $match_session_id, $account_id, $steam_id, $device_id, $seq, $received_at, $status, $latency_ms, $signals_json
            );
            """;
        cmd.Parameters.AddWithValue("$match_session_id", request.MatchSessionId);
        cmd.Parameters.AddWithValue("$account_id", request.AccountId);
        cmd.Parameters.AddWithValue("$steam_id", steamId);
        cmd.Parameters.AddWithValue("$device_id", request.DeviceId);
        cmd.Parameters.AddWithValue("$seq", request.Sequence);
        cmd.Parameters.AddWithValue("$received_at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$latency_ms", request.HeartbeatLatencyMs);
        cmd.Parameters.AddWithValue("$signals_json", JsonSerializer.Serialize(
            new
            {
                request.PlatformSignals,
                request.IntegritySignals
            },
            JsonOptions));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<HeartbeatDbRecord?> GetLatestHeartbeatAsync(
        string matchSessionId,
        string accountId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT match_session_id, account_id, steam_id, seq, received_at_utc, status
            FROM heartbeats
            WHERE match_session_id = $match_session_id
              AND account_id = $account_id
            ORDER BY id DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$match_session_id", matchSessionId);
        cmd.Parameters.AddWithValue("$account_id", accountId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new HeartbeatDbRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            DateTimeOffset.Parse(reader.GetString(4)),
            reader.GetString(5));
    }

    public async Task UpsertPlayerHealthAsync(
        string matchSessionId,
        string accountId,
        string steamId,
        string status,
        DateTimeOffset lastHeartbeatUtc,
        string recommendedAction,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO player_health (
                match_session_id, account_id, steam_id, status, last_heartbeat_utc, recommended_action
            )
            VALUES (
                $match_session_id, $account_id, $steam_id, $status, $last_heartbeat, $recommended_action
            )
            ON CONFLICT(match_session_id, account_id) DO UPDATE SET
                steam_id = excluded.steam_id,
                status = excluded.status,
                last_heartbeat_utc = excluded.last_heartbeat_utc,
                recommended_action = excluded.recommended_action;
            """;
        cmd.Parameters.AddWithValue("$match_session_id", matchSessionId);
        cmd.Parameters.AddWithValue("$account_id", accountId);
        cmd.Parameters.AddWithValue("$steam_id", steamId);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$last_heartbeat", lastHeartbeatUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$recommended_action", recommendedAction);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PlayerHealthState>> GetMatchHealthAsync(string matchSessionId, CancellationToken cancellationToken)
    {
        var result = new List<PlayerHealthState>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT account_id, steam_id, status, last_heartbeat_utc, recommended_action
            FROM player_health
            WHERE match_session_id = $match_session_id;
            """;
        cmd.Parameters.AddWithValue("$match_session_id", matchSessionId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PlayerHealthState(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                reader.GetString(4)));
        }

        return result;
    }

    public async Task AddTelemetryEventsAsync<T>(
        string matchSessionId,
        string eventType,
        IEnumerable<T> items,
        Func<T, string?> accountSelector,
        Func<T, long> tickSelector,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (var item in items)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO telemetry_events (
                    match_session_id, tick_id, event_type, account_id, payload_json, created_at_utc
                )
                VALUES (
                    $match_session_id, $tick_id, $event_type, $account_id, $payload_json, $created_at
                );
                """;
            cmd.Parameters.AddWithValue("$match_session_id", matchSessionId);
            cmd.Parameters.AddWithValue("$tick_id", tickSelector(item));
            cmd.Parameters.AddWithValue("$event_type", eventType);
            cmd.Parameters.AddWithValue("$account_id", accountSelector(item) ?? string.Empty);
            cmd.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(item, JsonOptions));
            cmd.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpsertSuspicionScoreAsync(SuspicionScoreUpdate update, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO suspicion_scores (
                match_session_id, account_id, channel, score, confidence, sample_size, updated_at_utc
            )
            VALUES (
                $match_session_id, $account_id, $channel, $score, $confidence, $sample_size, $updated_at
            )
            ON CONFLICT(match_session_id, account_id, channel) DO UPDATE SET
                score = excluded.score,
                confidence = excluded.confidence,
                sample_size = excluded.sample_size,
                updated_at_utc = excluded.updated_at_utc;
            """;
        cmd.Parameters.AddWithValue("$match_session_id", update.MatchSessionId);
        cmd.Parameters.AddWithValue("$account_id", update.AccountId);
        cmd.Parameters.AddWithValue("$channel", update.Channel);
        cmd.Parameters.AddWithValue("$score", update.Score);
        cmd.Parameters.AddWithValue("$confidence", update.Confidence);
        cmd.Parameters.AddWithValue("$sample_size", update.SampleSize);
        cmd.Parameters.AddWithValue("$updated_at", update.UpdatedAtUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SuspicionScoreUpdate>> GetSuspicionScoresAsync(
        string matchSessionId,
        string accountId,
        CancellationToken cancellationToken)
    {
        var result = new List<SuspicionScoreUpdate>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT channel, score, confidence, sample_size, updated_at_utc
            FROM suspicion_scores
            WHERE match_session_id = $match_session_id
              AND account_id = $account_id;
            """;
        cmd.Parameters.AddWithValue("$match_session_id", matchSessionId);
        cmd.Parameters.AddWithValue("$account_id", accountId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SuspicionScoreUpdate(
                matchSessionId,
                accountId,
                reader.GetString(0),
                reader.GetDouble(1),
                reader.GetDouble(2),
                reader.GetInt32(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }

        return result;
    }

    public async Task AddEnforcementActionAsync(EnforcementAction action, string detailsJson, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO enforcement_actions (
                action_id, match_session_id, account_id, action_type, reason_code, duration_seconds, created_at_utc, details_json
            )
            VALUES (
                $action_id, $match_session_id, $account_id, $action_type, $reason_code, $duration_seconds, $created_at, $details_json
            )
            ON CONFLICT(action_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$action_id", action.ActionId);
        cmd.Parameters.AddWithValue("$match_session_id", action.MatchSessionId);
        cmd.Parameters.AddWithValue("$account_id", action.AccountId);
        cmd.Parameters.AddWithValue("$action_type", action.ActionType);
        cmd.Parameters.AddWithValue("$reason_code", action.ReasonCode);
        cmd.Parameters.AddWithValue("$duration_seconds", action.DurationSeconds);
        cmd.Parameters.AddWithValue("$created_at", action.CreatedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$details_json", detailsJson);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EnforcementAction>> GetEnforcementActionsAsync(
        string matchSessionId,
        CancellationToken cancellationToken)
    {
        var result = new List<EnforcementAction>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT action_id, match_session_id, account_id, action_type, reason_code, duration_seconds, created_at_utc
            FROM enforcement_actions
            WHERE match_session_id = $match_session_id
            ORDER BY created_at_utc DESC;
            """;
        cmd.Parameters.AddWithValue("$match_session_id", matchSessionId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new EnforcementAction(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                DateTimeOffset.Parse(reader.GetString(6))));
        }

        return result;
    }
}

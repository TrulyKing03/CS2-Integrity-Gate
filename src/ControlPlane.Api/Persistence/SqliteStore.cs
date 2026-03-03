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
    Task<bool> IsDeviceBoundToAccountAsync(
        string deviceId,
        string accountId,
        string steamId,
        CancellationToken cancellationToken);
    Task CreateMatchSessionAsync(QueueResponse queueResponse, string accountId, string steamId, CancellationToken cancellationToken);
    Task<bool> IsMatchPlayerAsync(string matchSessionId, string accountId, string steamId, CancellationToken cancellationToken);
    Task<string?> GetSteamIdForMatchPlayerAsync(string matchSessionId, string accountId, CancellationToken cancellationToken);
    Task<string?> GetServerIdForMatchAsync(string matchSessionId, CancellationToken cancellationToken);
    Task<string?> GetQueueTypeForMatchAsync(string matchSessionId, CancellationToken cancellationToken);
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
    Task<IReadOnlyList<EnforcementAction>> GetPendingEnforcementActionsAsync(string matchSessionId, string? accountId, CancellationToken cancellationToken);
    Task<bool> AcknowledgeEnforcementActionAsync(EnforcementActionAckRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<TelemetryEventRecord>> GetRecentTelemetryEventsAsync(string matchSessionId, string accountId, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<HeartbeatDbRecord>> GetRecentHeartbeatsAsync(string matchSessionId, string accountId, int limit, CancellationToken cancellationToken);
    Task SaveEvidencePackSummaryAsync(EvidencePackSummary summary, CancellationToken cancellationToken);
    Task<EvidencePackSummary?> GetEvidencePackSummaryAsync(string evidenceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<EvidencePackSummary>> ListEvidencePackSummariesAsync(string? matchSessionId, string? accountId, CancellationToken cancellationToken);
    Task<ReviewCaseSummary> CreateReviewCaseAsync(CreateReviewCaseRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReviewCaseSummary>> ListReviewCasesAsync(string? status, CancellationToken cancellationToken);
    Task<ReviewCaseSummary?> UpdateReviewCaseAsync(UpdateReviewCaseRequest request, CancellationToken cancellationToken);
    Task<BanRecord> CreateBanAsync(CreateBanRequest request, CancellationToken cancellationToken);
    Task<BanRecord?> GetBanAsync(string banId, CancellationToken cancellationToken);
    Task<BanRecord?> GetActiveBanForAccountAsync(string accountId, CancellationToken cancellationToken);
    Task<IReadOnlyList<BanRecord>> ListBansAsync(string? accountId, string? status, CancellationToken cancellationToken);
    Task<BanRecord?> UpdateBanStatusAsync(UpdateBanStatusRequest request, CancellationToken cancellationToken);
    Task<AppealRecord> CreateAppealAsync(CreateAppealRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<AppealRecord>> ListAppealsAsync(string? status, CancellationToken cancellationToken);
    Task<AppealRecord?> ResolveAppealAsync(ResolveAppealRequest request, CancellationToken cancellationToken);
    Task<SystemSummaryMetrics> GetSystemSummaryMetricsAsync(CancellationToken cancellationToken);
}

public sealed record JoinTokenDbRecord(string Jti, string PayloadJson, DateTimeOffset? UsedAtUtc);

public sealed record HeartbeatDbRecord(
    string MatchSessionId,
    string AccountId,
    string SteamId,
    long Sequence,
    DateTimeOffset ReceivedAtUtc,
    string Status);

public sealed record TelemetryEventRecord(
    long TickId,
    string EventType,
    string AccountId,
    string PayloadJson,
    DateTimeOffset CreatedAtUtc);

public sealed record SystemSummaryMetrics(
    int AccountCount,
    int MatchSessionCount,
    int ActiveBanCount,
    int PendingEnforcementActionCount,
    int OpenReviewCaseCount,
    int SubmittedAppealCount,
    int EvidencePackCount,
    DateTimeOffset? LastHeartbeatUtc,
    DateTimeOffset? LastTelemetryUtc);

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

            CREATE TABLE IF NOT EXISTS enforcement_action_acks (
                action_id TEXT PRIMARY KEY,
                match_session_id TEXT NOT NULL,
                account_id TEXT NOT NULL,
                executor_id TEXT NOT NULL,
                result TEXT NOT NULL,
                notes TEXT NOT NULL,
                acked_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS evidence_packs (
                evidence_id TEXT PRIMARY KEY,
                match_session_id TEXT NOT NULL,
                account_id TEXT NOT NULL,
                trigger_type TEXT NOT NULL,
                action_id TEXT NULL,
                storage_path TEXT NOT NULL,
                content_sha256 TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                review_status TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_evidence_match_account
                ON evidence_packs (match_session_id, account_id, created_at_utc DESC);

            CREATE TABLE IF NOT EXISTS review_cases (
                case_id TEXT PRIMARY KEY,
                evidence_id TEXT NOT NULL,
                match_session_id TEXT NOT NULL,
                account_id TEXT NOT NULL,
                reason_code TEXT NOT NULL,
                priority TEXT NOT NULL,
                status TEXT NOT NULL,
                assigned_reviewer TEXT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS review_case_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                case_id TEXT NOT NULL,
                actor_id TEXT NOT NULL,
                action_type TEXT NOT NULL,
                notes TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS bans (
                ban_id TEXT PRIMARY KEY,
                account_id TEXT NOT NULL,
                scope TEXT NOT NULL,
                status TEXT NOT NULL,
                start_at_utc TEXT NOT NULL,
                end_at_utc TEXT NULL,
                reason TEXT NOT NULL,
                evidence_id TEXT NULL,
                created_by TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_bans_account_status
                ON bans (account_id, status, start_at_utc, end_at_utc);

            CREATE TABLE IF NOT EXISTS ban_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ban_id TEXT NOT NULL,
                actor_id TEXT NOT NULL,
                action_type TEXT NOT NULL,
                notes TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS appeals (
                appeal_id TEXT PRIMARY KEY,
                ban_id TEXT NOT NULL,
                account_id TEXT NOT NULL,
                status TEXT NOT NULL,
                notes TEXT NOT NULL,
                submitted_at_utc TEXT NOT NULL,
                decision_at_utc TEXT NULL,
                reviewer_id TEXT NULL,
                decision_notes TEXT NULL
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

    public async Task<bool> IsDeviceBoundToAccountAsync(
        string deviceId,
        string accountId,
        string steamId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1)
            FROM devices
            WHERE device_id = $device_id
              AND account_id = $account_id
              AND steam_id = $steam_id
              AND status = 'enrolled';
            """;
        cmd.Parameters.AddWithValue("$device_id", deviceId);
        cmd.Parameters.AddWithValue("$account_id", accountId);
        cmd.Parameters.AddWithValue("$steam_id", steamId);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        return count > 0;
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

    public async Task<string?> GetQueueTypeForMatchAsync(string matchSessionId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT queue_type
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

    public async Task<IReadOnlyList<EnforcementAction>> GetPendingEnforcementActionsAsync(
        string matchSessionId,
        string? accountId,
        CancellationToken cancellationToken)
    {
        var result = new List<EnforcementAction>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();

        var query = """
            SELECT ea.action_id, ea.match_session_id, ea.account_id, ea.action_type, ea.reason_code, ea.duration_seconds, ea.created_at_utc
            FROM enforcement_actions ea
            LEFT JOIN enforcement_action_acks ack
                ON ack.action_id = ea.action_id
            WHERE ea.match_session_id = $match_session_id
              AND ack.action_id IS NULL
            """;
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            query += """
            
              AND ea.account_id = $account_id
            """;
            cmd.Parameters.AddWithValue("$account_id", accountId);
        }

        query += """
            
            ORDER BY ea.created_at_utc DESC;
            """;
        cmd.CommandText = query;
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

    public async Task<bool> AcknowledgeEnforcementActionAsync(
        EnforcementActionAckRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO enforcement_action_acks (
                action_id, match_session_id, account_id, executor_id, result, notes, acked_at_utc
            )
            VALUES (
                $action_id, $match_session_id, $account_id, $executor_id, $result, $notes, $acked_at_utc
            )
            ON CONFLICT(action_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$action_id", request.ActionId);
        cmd.Parameters.AddWithValue("$match_session_id", request.MatchSessionId);
        cmd.Parameters.AddWithValue("$account_id", request.AccountId);
        cmd.Parameters.AddWithValue("$executor_id", request.ExecutorId);
        cmd.Parameters.AddWithValue("$result", request.Result);
        cmd.Parameters.AddWithValue("$notes", request.Notes);
        cmd.Parameters.AddWithValue("$acked_at_utc", request.AckedAtUtc.ToString("O"));

        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<IReadOnlyList<TelemetryEventRecord>> GetRecentTelemetryEventsAsync(
        string matchSessionId,
        string accountId,
        int limit,
        CancellationToken cancellationToken)
    {
        var result = new List<TelemetryEventRecord>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT tick_id, event_type, account_id, payload_json, created_at_utc
            FROM telemetry_events
            WHERE match_session_id = $match_session_id
              AND account_id = $account_id
            ORDER BY id DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$match_session_id", matchSessionId);
        cmd.Parameters.AddWithValue("$account_id", accountId);
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TelemetryEventRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }

        return result;
    }

    public async Task<IReadOnlyList<HeartbeatDbRecord>> GetRecentHeartbeatsAsync(
        string matchSessionId,
        string accountId,
        int limit,
        CancellationToken cancellationToken)
    {
        var result = new List<HeartbeatDbRecord>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT match_session_id, account_id, steam_id, seq, received_at_utc, status
            FROM heartbeats
            WHERE match_session_id = $match_session_id
              AND account_id = $account_id
            ORDER BY id DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$match_session_id", matchSessionId);
        cmd.Parameters.AddWithValue("$account_id", accountId);
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new HeartbeatDbRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.GetString(5)));
        }

        return result;
    }

    public async Task SaveEvidencePackSummaryAsync(EvidencePackSummary summary, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO evidence_packs (
                evidence_id, match_session_id, account_id, trigger_type, action_id,
                storage_path, content_sha256, created_at_utc, review_status
            )
            VALUES (
                $evidence_id, $match_session_id, $account_id, $trigger_type, $action_id,
                $storage_path, $content_sha256, $created_at_utc, $review_status
            )
            ON CONFLICT(evidence_id) DO UPDATE SET
                review_status = excluded.review_status,
                storage_path = excluded.storage_path,
                content_sha256 = excluded.content_sha256;
            """;
        cmd.Parameters.AddWithValue("$evidence_id", summary.EvidenceId);
        cmd.Parameters.AddWithValue("$match_session_id", summary.MatchSessionId);
        cmd.Parameters.AddWithValue("$account_id", summary.AccountId);
        cmd.Parameters.AddWithValue("$trigger_type", summary.TriggerType);
        cmd.Parameters.AddWithValue("$action_id", (object?)summary.ActionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$storage_path", summary.StoragePath);
        cmd.Parameters.AddWithValue("$content_sha256", summary.ContentSha256);
        cmd.Parameters.AddWithValue("$created_at_utc", summary.CreatedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$review_status", summary.ReviewStatus);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<EvidencePackSummary?> GetEvidencePackSummaryAsync(string evidenceId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT evidence_id, match_session_id, account_id, trigger_type, action_id, storage_path, content_sha256, created_at_utc, review_status
            FROM evidence_packs
            WHERE evidence_id = $evidence_id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$evidence_id", evidenceId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadEvidenceSummary(reader);
    }

    public async Task<IReadOnlyList<EvidencePackSummary>> ListEvidencePackSummariesAsync(
        string? matchSessionId,
        string? accountId,
        CancellationToken cancellationToken)
    {
        var result = new List<EvidencePackSummary>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();

        var query = """
            SELECT evidence_id, match_session_id, account_id, trigger_type, action_id, storage_path, content_sha256, created_at_utc, review_status
            FROM evidence_packs
            WHERE 1 = 1
            """;
        if (!string.IsNullOrWhiteSpace(matchSessionId))
        {
            query += """
            
              AND match_session_id = $match_session_id
            """;
            cmd.Parameters.AddWithValue("$match_session_id", matchSessionId);
        }

        if (!string.IsNullOrWhiteSpace(accountId))
        {
            query += """
            
              AND account_id = $account_id
            """;
            cmd.Parameters.AddWithValue("$account_id", accountId);
        }

        query += """
            
            ORDER BY created_at_utc DESC
            LIMIT 500;
            """;

        cmd.CommandText = query;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadEvidenceSummary(reader));
        }

        return result;
    }

    public async Task<ReviewCaseSummary> CreateReviewCaseAsync(CreateReviewCaseRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var caseId = $"case_{Guid.NewGuid():N}";
        var status = "open";

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO review_cases (
                    case_id, evidence_id, match_session_id, account_id, reason_code,
                    priority, status, assigned_reviewer, created_at_utc, updated_at_utc
                )
                VALUES (
                    $case_id, $evidence_id, $match_session_id, $account_id, $reason_code,
                    $priority, $status, NULL, $created_at_utc, $updated_at_utc
                );
                """;
            cmd.Parameters.AddWithValue("$case_id", caseId);
            cmd.Parameters.AddWithValue("$evidence_id", request.EvidenceId);
            cmd.Parameters.AddWithValue("$match_session_id", request.MatchSessionId);
            cmd.Parameters.AddWithValue("$account_id", request.AccountId);
            cmd.Parameters.AddWithValue("$reason_code", request.ReasonCode);
            cmd.Parameters.AddWithValue("$priority", request.Priority);
            cmd.Parameters.AddWithValue("$status", status);
            cmd.Parameters.AddWithValue("$created_at_utc", now.ToString("O"));
            cmd.Parameters.AddWithValue("$updated_at_utc", now.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO review_case_events (case_id, actor_id, action_type, notes, created_at_utc)
                VALUES ($case_id, $actor_id, $action_type, $notes, $created_at_utc);
                """;
            cmd.Parameters.AddWithValue("$case_id", caseId);
            cmd.Parameters.AddWithValue("$actor_id", request.RequestedBy);
            cmd.Parameters.AddWithValue("$action_type", "case_created");
            cmd.Parameters.AddWithValue("$notes", $"Created with reason={request.ReasonCode}");
            cmd.Parameters.AddWithValue("$created_at_utc", now.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        return new ReviewCaseSummary(
            caseId,
            request.EvidenceId,
            request.MatchSessionId,
            request.AccountId,
            request.ReasonCode,
            request.Priority,
            status,
            null,
            now,
            now);
    }

    public async Task<IReadOnlyList<ReviewCaseSummary>> ListReviewCasesAsync(string? status, CancellationToken cancellationToken)
    {
        var result = new List<ReviewCaseSummary>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        var query = """
            SELECT case_id, evidence_id, match_session_id, account_id, reason_code, priority, status, assigned_reviewer, created_at_utc, updated_at_utc
            FROM review_cases
            WHERE 1 = 1
            """;
        if (!string.IsNullOrWhiteSpace(status))
        {
            query += """
            
              AND status = $status
            """;
            cmd.Parameters.AddWithValue("$status", status);
        }

        query += """
            
            ORDER BY updated_at_utc DESC
            LIMIT 500;
            """;
        cmd.CommandText = query;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadReviewCaseSummary(reader));
        }

        return result;
    }

    public async Task<ReviewCaseSummary?> UpdateReviewCaseAsync(UpdateReviewCaseRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var current = await GetReviewCaseByIdInternalAsync(connection, tx, request.CaseId, cancellationToken);
        if (current is null)
        {
            return null;
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE review_cases
                SET status = $status,
                    assigned_reviewer = $assigned_reviewer,
                    updated_at_utc = $updated_at_utc
                WHERE case_id = $case_id;
                """;
            cmd.Parameters.AddWithValue("$status", request.Status);
            cmd.Parameters.AddWithValue("$assigned_reviewer", request.ReviewerId);
            cmd.Parameters.AddWithValue("$updated_at_utc", now.ToString("O"));
            cmd.Parameters.AddWithValue("$case_id", request.CaseId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO review_case_events (case_id, actor_id, action_type, notes, created_at_utc)
                VALUES ($case_id, $actor_id, $action_type, $notes, $created_at_utc);
                """;
            cmd.Parameters.AddWithValue("$case_id", request.CaseId);
            cmd.Parameters.AddWithValue("$actor_id", request.ReviewerId);
            cmd.Parameters.AddWithValue("$action_type", "case_updated");
            cmd.Parameters.AddWithValue("$notes", request.Notes);
            cmd.Parameters.AddWithValue("$created_at_utc", now.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return current with
        {
            Status = request.Status,
            AssignedReviewer = request.ReviewerId,
            UpdatedAtUtc = now
        };
    }

    public async Task<BanRecord> CreateBanAsync(CreateBanRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var ban = new BanRecord(
            $"ban_{Guid.NewGuid():N}",
            request.AccountId,
            request.Scope,
            "active",
            request.StartAtUtc,
            request.EndAtUtc,
            request.Reason,
            request.EvidenceId,
            now);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO bans (
                    ban_id, account_id, scope, status, start_at_utc, end_at_utc, reason, evidence_id, created_by, created_at_utc
                )
                VALUES (
                    $ban_id, $account_id, $scope, $status, $start_at_utc, $end_at_utc, $reason, $evidence_id, $created_by, $created_at_utc
                );
                """;
            cmd.Parameters.AddWithValue("$ban_id", ban.BanId);
            cmd.Parameters.AddWithValue("$account_id", ban.AccountId);
            cmd.Parameters.AddWithValue("$scope", ban.Scope);
            cmd.Parameters.AddWithValue("$status", ban.Status);
            cmd.Parameters.AddWithValue("$start_at_utc", ban.StartAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$end_at_utc", (object?)ban.EndAtUtc?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$reason", ban.Reason);
            cmd.Parameters.AddWithValue("$evidence_id", (object?)ban.EvidenceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created_by", request.CreatedBy);
            cmd.Parameters.AddWithValue("$created_at_utc", ban.CreatedAtUtc.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO ban_events (ban_id, actor_id, action_type, notes, created_at_utc)
                VALUES ($ban_id, $actor_id, $action_type, $notes, $created_at_utc);
                """;
            cmd.Parameters.AddWithValue("$ban_id", ban.BanId);
            cmd.Parameters.AddWithValue("$actor_id", request.CreatedBy);
            cmd.Parameters.AddWithValue("$action_type", "ban_created");
            cmd.Parameters.AddWithValue("$notes", $"scope={ban.Scope}; reason={ban.Reason}");
            cmd.Parameters.AddWithValue("$created_at_utc", now.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return ban;
    }

    public async Task<BanRecord?> GetBanAsync(string banId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ban_id, account_id, scope, status, start_at_utc, end_at_utc, reason, evidence_id, created_at_utc
            FROM bans
            WHERE ban_id = $ban_id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$ban_id", banId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadBanRecord(reader);
    }

    public async Task<BanRecord?> GetActiveBanForAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ban_id, account_id, scope, status, start_at_utc, end_at_utc, reason, evidence_id, created_at_utc
            FROM bans
            WHERE account_id = $account_id
              AND status = 'active'
              AND start_at_utc <= $now_utc
              AND (end_at_utc IS NULL OR end_at_utc > $now_utc)
            ORDER BY created_at_utc DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$account_id", accountId);
        cmd.Parameters.AddWithValue("$now_utc", now.ToString("O"));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadBanRecord(reader);
    }

    public async Task<IReadOnlyList<BanRecord>> ListBansAsync(string? accountId, string? status, CancellationToken cancellationToken)
    {
        var result = new List<BanRecord>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();

        var query = """
            SELECT ban_id, account_id, scope, status, start_at_utc, end_at_utc, reason, evidence_id, created_at_utc
            FROM bans
            WHERE 1 = 1
            """;
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            query += """
            
              AND account_id = $account_id
            """;
            cmd.Parameters.AddWithValue("$account_id", accountId);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query += """
            
              AND status = $status
            """;
            cmd.Parameters.AddWithValue("$status", status);
        }

        query += """
            
            ORDER BY created_at_utc DESC
            LIMIT 500;
            """;
        cmd.CommandText = query;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadBanRecord(reader));
        }

        return result;
    }

    public async Task<BanRecord?> UpdateBanStatusAsync(UpdateBanStatusRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE bans
                SET status = $status,
                    end_at_utc = $end_at_utc
                WHERE ban_id = $ban_id;
                """;
            cmd.Parameters.AddWithValue("$status", request.Status);
            cmd.Parameters.AddWithValue("$end_at_utc", (object?)request.EndAtUtc?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ban_id", request.BanId);
            var updated = await cmd.ExecuteNonQueryAsync(cancellationToken);
            if (updated == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return null;
            }
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO ban_events (ban_id, actor_id, action_type, notes, created_at_utc)
                VALUES ($ban_id, $actor_id, $action_type, $notes, $created_at_utc);
                """;
            cmd.Parameters.AddWithValue("$ban_id", request.BanId);
            cmd.Parameters.AddWithValue("$actor_id", request.UpdatedBy);
            cmd.Parameters.AddWithValue("$action_type", $"ban_{request.Status}");
            cmd.Parameters.AddWithValue("$notes", request.Notes);
            cmd.Parameters.AddWithValue("$created_at_utc", now.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return await GetBanAsync(request.BanId, cancellationToken);
    }

    public async Task<AppealRecord> CreateAppealAsync(CreateAppealRequest request, CancellationToken cancellationToken)
    {
        var appeal = new AppealRecord(
            $"appeal_{Guid.NewGuid():N}",
            request.BanId,
            request.AccountId,
            "submitted",
            DateTimeOffset.UtcNow,
            null,
            null,
            null);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO appeals (
                appeal_id, ban_id, account_id, status, notes, submitted_at_utc, decision_at_utc, reviewer_id, decision_notes
            )
            VALUES (
                $appeal_id, $ban_id, $account_id, $status, $notes, $submitted_at_utc, NULL, NULL, NULL
            );
            """;
        cmd.Parameters.AddWithValue("$appeal_id", appeal.AppealId);
        cmd.Parameters.AddWithValue("$ban_id", appeal.BanId);
        cmd.Parameters.AddWithValue("$account_id", appeal.AccountId);
        cmd.Parameters.AddWithValue("$status", appeal.Status);
        cmd.Parameters.AddWithValue("$notes", request.Notes);
        cmd.Parameters.AddWithValue("$submitted_at_utc", appeal.SubmittedAtUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return appeal;
    }

    public async Task<IReadOnlyList<AppealRecord>> ListAppealsAsync(string? status, CancellationToken cancellationToken)
    {
        var result = new List<AppealRecord>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        var query = """
            SELECT appeal_id, ban_id, account_id, status, submitted_at_utc, decision_at_utc, reviewer_id, decision_notes
            FROM appeals
            WHERE 1 = 1
            """;
        if (!string.IsNullOrWhiteSpace(status))
        {
            query += """
            
              AND status = $status
            """;
            cmd.Parameters.AddWithValue("$status", status);
        }

        query += """
            
            ORDER BY submitted_at_utc DESC
            LIMIT 500;
            """;
        cmd.CommandText = query;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AppealRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return result;
    }

    public async Task<AppealRecord?> ResolveAppealAsync(ResolveAppealRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE appeals
                SET status = $status,
                    decision_at_utc = $decision_at_utc,
                    reviewer_id = $reviewer_id,
                    decision_notes = $decision_notes
                WHERE appeal_id = $appeal_id;
                """;
            cmd.Parameters.AddWithValue("$status", request.Status);
            cmd.Parameters.AddWithValue("$decision_at_utc", now.ToString("O"));
            cmd.Parameters.AddWithValue("$reviewer_id", request.ReviewerId);
            cmd.Parameters.AddWithValue("$decision_notes", request.DecisionNotes);
            cmd.Parameters.AddWithValue("$appeal_id", request.AppealId);
            var updated = await cmd.ExecuteNonQueryAsync(cancellationToken);
            if (updated == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return null;
            }
        }

        AppealRecord? appeal;
        await using (var readCmd = connection.CreateCommand())
        {
            readCmd.Transaction = tx;
            readCmd.CommandText = """
                SELECT appeal_id, ban_id, account_id, status, submitted_at_utc, decision_at_utc, reviewer_id, decision_notes
                FROM appeals
                WHERE appeal_id = $appeal_id
                LIMIT 1;
                """;
            readCmd.Parameters.AddWithValue("$appeal_id", request.AppealId);
            await using var reader = await readCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return null;
            }

            appeal = new AppealRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7));
        }

        if (string.Equals(request.Status, "overturned", StringComparison.OrdinalIgnoreCase))
        {
            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE bans
                    SET status = 'revoked',
                        end_at_utc = $end_at_utc
                    WHERE ban_id = $ban_id
                      AND status = 'active';
                    """;
                cmd.Parameters.AddWithValue("$end_at_utc", now.ToString("O"));
                cmd.Parameters.AddWithValue("$ban_id", appeal.BanId);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO ban_events (ban_id, actor_id, action_type, notes, created_at_utc)
                    VALUES ($ban_id, $actor_id, $action_type, $notes, $created_at_utc);
                    """;
                cmd.Parameters.AddWithValue("$ban_id", appeal.BanId);
                cmd.Parameters.AddWithValue("$actor_id", request.ReviewerId);
                cmd.Parameters.AddWithValue("$action_type", "ban_revoked_via_appeal");
                cmd.Parameters.AddWithValue("$notes", request.DecisionNotes);
                cmd.Parameters.AddWithValue("$created_at_utc", now.ToString("O"));
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await tx.CommitAsync(cancellationToken);
        return appeal;
    }

    public async Task<SystemSummaryMetrics> GetSystemSummaryMetricsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                (SELECT COUNT(1) FROM accounts) AS account_count,
                (SELECT COUNT(1) FROM match_sessions) AS match_session_count,
                (
                    SELECT COUNT(1)
                    FROM bans
                    WHERE status = 'active'
                      AND start_at_utc <= $now_utc
                      AND (end_at_utc IS NULL OR end_at_utc > $now_utc)
                ) AS active_ban_count,
                (
                    SELECT COUNT(1)
                    FROM enforcement_actions e
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM enforcement_action_acks a
                        WHERE a.action_id = e.action_id
                    )
                ) AS pending_actions_count,
                (
                    SELECT COUNT(1)
                    FROM review_cases
                    WHERE status IN ('open', 'in_review')
                ) AS open_review_case_count,
                (
                    SELECT COUNT(1)
                    FROM appeals
                    WHERE status = 'submitted'
                ) AS submitted_appeal_count,
                (SELECT COUNT(1) FROM evidence_packs) AS evidence_pack_count,
                (SELECT MAX(received_at_utc) FROM heartbeats) AS last_heartbeat_utc,
                (SELECT MAX(created_at_utc) FROM telemetry_events) AS last_telemetry_utc;
            """;
        cmd.Parameters.AddWithValue("$now_utc", now);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new SystemSummaryMetrics(0, 0, 0, 0, 0, 0, 0, null, null);
        }

        return new SystemSummaryMetrics(
            AccountCount: reader.GetInt32(0),
            MatchSessionCount: reader.GetInt32(1),
            ActiveBanCount: reader.GetInt32(2),
            PendingEnforcementActionCount: reader.GetInt32(3),
            OpenReviewCaseCount: reader.GetInt32(4),
            SubmittedAppealCount: reader.GetInt32(5),
            EvidencePackCount: reader.GetInt32(6),
            LastHeartbeatUtc: reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
            LastTelemetryUtc: reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)));
    }

    private static EvidencePackSummary ReadEvidenceSummary(SqliteDataReader reader)
    {
        return new EvidencePackSummary(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7)),
            reader.GetString(8));
    }

    private static ReviewCaseSummary ReadReviewCaseSummary(SqliteDataReader reader)
    {
        return new ReviewCaseSummary(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8)),
            DateTimeOffset.Parse(reader.GetString(9)));
    }

    private static BanRecord ReadBanRecord(SqliteDataReader reader)
    {
        return new BanRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8)));
    }

    private static async Task<ReviewCaseSummary?> GetReviewCaseByIdInternalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string caseId,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT case_id, evidence_id, match_session_id, account_id, reason_code, priority, status, assigned_reviewer, created_at_utc, updated_at_utc
            FROM review_cases
            WHERE case_id = $case_id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$case_id", caseId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadReviewCaseSummary(reader);
    }
}

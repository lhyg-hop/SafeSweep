using System.IO;
using Microsoft.Data.Sqlite;
using SafeSweep.Models;

namespace SafeSweep.Services.Storage;

public sealed class SqliteAuditRepository : IAuditRepository
{
    private readonly string _connectionString;

    public SqliteAuditRepository(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString();

        Initialize();
    }

    public async Task<IReadOnlyCollection<string>> GetIgnoredPathsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT path FROM ignored_paths;";
        var results = new List<string>();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    public async Task AddIgnoredPathAsync(string path, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT OR REPLACE INTO ignored_paths(path, added_at)
            VALUES ($path, $addedAt);
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$addedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveActionRecordAsync(ActionRecord record, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT OR REPLACE INTO action_records(
                action_id, plan_id, path, operation_type, original_path, quarantine_path,
                bytes_freed, started_at, ended_at, status, error_code, can_restore, summary)
            VALUES (
                $actionId, $planId, $path, $operationType, $originalPath, $quarantinePath,
                $bytesFreed, $startedAt, $endedAt, $status, $errorCode, $canRestore, $summary);
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$actionId", record.ActionId);
        command.Parameters.AddWithValue("$planId", record.PlanId);
        command.Parameters.AddWithValue("$path", record.Path);
        command.Parameters.AddWithValue("$operationType", record.OperationType.ToString());
        command.Parameters.AddWithValue("$originalPath", (object?)record.OriginalPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$quarantinePath", (object?)record.QuarantinePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$bytesFreed", record.BytesFreed);
        command.Parameters.AddWithValue("$startedAt", record.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$endedAt", record.EndedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", record.Status.ToString());
        command.Parameters.AddWithValue("$errorCode", (object?)record.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$canRestore", record.CanRestore ? 1 : 0);
        command.Parameters.AddWithValue("$summary", record.Summary);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveRestoreRecordAsync(RestoreRecord record, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT OR REPLACE INTO restore_records(action_id, restore_status, conflict_policy, restored_at)
            VALUES ($actionId, $status, $policy, $restoredAt);
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$actionId", record.ActionId);
        command.Parameters.AddWithValue("$status", record.RestoreStatus.ToString());
        command.Parameters.AddWithValue("$policy", record.ConflictPolicy.ToString());
        command.Parameters.AddWithValue("$restoredAt", record.RestoredAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ActionRecord>> GetActionRecordsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT action_id, plan_id, path, operation_type, original_path, quarantine_path,
                   bytes_freed, started_at, ended_at, status, error_code, can_restore, summary
            FROM action_records
            ORDER BY started_at DESC;
            """;

        var results = new List<ActionRecord>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ActionRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                Enum.Parse<ActionPolicy>(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt64(6),
                DateTimeOffset.Parse(reader.GetString(7)),
                reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
                Enum.Parse<ActionRecordStatus>(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetInt64(11) == 1,
                reader.GetString(12)));
        }

        return results;
    }

    public async Task SaveScanSnapshotAsync(string sessionId, ScanMode mode, IEnumerable<ScanFinding> findings, CancellationToken cancellationToken = default)
    {
        const string sessionSql = """
            INSERT OR REPLACE INTO scan_sessions(session_id, started_at, mode)
            VALUES ($sessionId, $startedAt, $mode);
            """;
        const string deleteExistingSql = "DELETE FROM scan_findings WHERE session_id = $sessionId;";
        const string insertSql = """
            INSERT INTO scan_findings(session_id, source, category, path, size)
            VALUES ($sessionId, $source, $category, $path, $size);
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var sessionCommand = connection.CreateCommand())
        {
            sessionCommand.Transaction = transaction;
            sessionCommand.CommandText = sessionSql;
            sessionCommand.Parameters.AddWithValue("$sessionId", sessionId);
            sessionCommand.Parameters.AddWithValue("$startedAt", DateTimeOffset.UtcNow.ToString("O"));
            sessionCommand.Parameters.AddWithValue("$mode", mode.ToString());
            await sessionCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = deleteExistingSql;
            deleteCommand.Parameters.AddWithValue("$sessionId", sessionId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var finding in findings)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = insertSql;
            insertCommand.Parameters.AddWithValue("$sessionId", sessionId);
            insertCommand.Parameters.AddWithValue("$source", finding.Source);
            insertCommand.Parameters.AddWithValue("$category", finding.Category);
            insertCommand.Parameters.AddWithValue("$path", finding.Path);
            insertCommand.Parameters.AddWithValue("$size", finding.Size);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ScanSnapshotSummary>> GetGrowthSummariesAsync(CancellationToken cancellationToken = default)
    {
        const string sessionsSql = """
            SELECT session_id
            FROM scan_sessions
            ORDER BY started_at DESC
            LIMIT 2;
            """;

        var sessionIds = new List<string>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using (var sessionCommand = connection.CreateCommand())
        {
            sessionCommand.CommandText = sessionsSql;
            await using var reader = await sessionCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sessionIds.Add(reader.GetString(0));
            }
        }

        if (sessionIds.Count == 0)
        {
            return [];
        }

        var current = await GetSummaryForSessionAsync(connection, sessionIds[0], cancellationToken);
        var previous = sessionIds.Count > 1
            ? await GetSummaryForSessionAsync(connection, sessionIds[1], cancellationToken)
            : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        var summaries = current
            .Select(kvp => new ScanSnapshotSummary(
                kvp.Key,
                kvp.Value,
                previous.TryGetValue(kvp.Key, out var previousBytes) ? previousBytes : 0))
            .OrderByDescending(item => item.DeltaBytes)
            .ToList();

        return summaries;
    }

    private void Initialize()
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ignored_paths (
                path TEXT PRIMARY KEY,
                added_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS action_records (
                action_id TEXT PRIMARY KEY,
                plan_id TEXT NOT NULL,
                path TEXT NOT NULL,
                operation_type TEXT NOT NULL,
                original_path TEXT NULL,
                quarantine_path TEXT NULL,
                bytes_freed INTEGER NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT NULL,
                status TEXT NOT NULL,
                error_code TEXT NULL,
                can_restore INTEGER NOT NULL,
                summary TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS restore_records (
                action_id TEXT PRIMARY KEY,
                restore_status TEXT NOT NULL,
                conflict_policy TEXT NOT NULL,
                restored_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS scan_sessions (
                session_id TEXT PRIMARY KEY,
                started_at TEXT NOT NULL,
                mode TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS scan_findings (
                session_id TEXT NOT NULL,
                source TEXT NOT NULL,
                category TEXT NOT NULL,
                path TEXT NOT NULL,
                size INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static async Task<Dictionary<string, long>> GetSummaryForSessionAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT source, SUM(size)
            FROM scan_findings
            WHERE session_id = $sessionId
            GROUP BY source;
            """;

        var results = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results[reader.GetString(0)] = reader.GetInt64(1);
        }

        return results;
    }
}

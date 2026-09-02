using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Jarvis.Core.ProjectLearning;
using Jarvis.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Jarvis.Infrastructure.ProjectLearning;

internal sealed class SqliteProjectLearningSessionStore : IProjectLearningSessionStore, IDisposable
{
    private const int MaximumPayloadBytes = 512 * 1024;
    private readonly ProjectLearningOptions _options;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ProjectLearningSessionSnapshot> _memory = [];
    private readonly object _memoryGate = new();
    private bool _initialized;

    public SqliteProjectLearningSessionStore(
        JarvisDataPaths paths,
        IOptions<ProjectLearningOptions> options)
    {
        _options = options.Value;
        string databasePath = JarvisDataPaths.ResolveUnder(
            paths.ProjectLearningData,
            "project-learning.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
    }

    public void Dispose() => _gate.Dispose();

    public async ValueTask SaveAsync(
        ProjectLearningSessionSnapshot session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_options.PersistSessions)
        {
            lock (_memoryGate)
            {
                EnsureMemoryCapacity(session.SessionId);
                _memory[session.SessionId] = session;
            }
            return;
        }

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(session, ProjectLearningJson.Options);
        if (payload.Length > MaximumPayloadBytes)
        {
            throw new ProjectLearningException("learning_session_too_large");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureCapacityAsync(connection, session.SessionId, cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO learning_sessions " +
                "(session_id, repository_path, kind, status, updated_utc, payload_json) " +
                "VALUES ($id, $path, $kind, $status, $updated, $payload) " +
                "ON CONFLICT(session_id) DO UPDATE SET " +
                "repository_path=excluded.repository_path, kind=excluded.kind, " +
                "status=excluded.status, updated_utc=excluded.updated_utc, payload_json=excluded.payload_json;";
            command.Parameters.AddWithValue("$id", session.SessionId.ToString("D", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$path", session.RepositoryPath);
            command.Parameters.AddWithValue("$kind", (int)session.Kind);
            command.Parameters.AddWithValue("$status", (int)session.Status);
            command.Parameters.AddWithValue("$updated", session.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.Add("$payload", SqliteType.Blob).Value = payload;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ProjectLearningSessionSnapshot?> LoadAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (!_options.PersistSessions)
        {
            lock (_memoryGate)
            {
                return _memory.GetValueOrDefault(sessionId);
            }
        }

        return await LoadOneAsync(
            "SELECT payload_json FROM learning_sessions WHERE session_id = $value;",
            sessionId.ToString("D", CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ProjectLearningSessionSnapshot?> LoadLatestCompletedInterviewAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        if (!_options.PersistSessions)
        {
            lock (_memoryGate)
            {
                return _memory.Values
                    .Where(session => session.Kind == LearningSessionKind.Interview &&
                        session.Status == LearningSessionStatus.Completed &&
                        string.Equals(session.RepositoryPath, repositoryPath, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(static session => session.UpdatedAt)
                    .FirstOrDefault();
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT payload_json FROM learning_sessions " +
                "WHERE repository_path = $path COLLATE NOCASE AND kind = $kind AND status = $status " +
                "ORDER BY updated_utc DESC LIMIT 1;";
            command.Parameters.AddWithValue("$path", repositoryPath);
            command.Parameters.AddWithValue("$kind", (int)LearningSessionKind.Interview);
            command.Parameters.AddWithValue("$status", (int)LearningSessionStatus.Completed);
            object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is byte[] payload ? Deserialize(payload) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ProjectLearningSessionSnapshot?> LoadOneAsync(
        string sql,
        string value,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$value", value);
            object? payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return payload is byte[] bytes ? Deserialize(bytes) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        string dataSource = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        Directory.CreateDirectory(Path.GetDirectoryName(dataSource)!);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS learning_sessions (" +
            "session_id TEXT PRIMARY KEY, repository_path TEXT NOT NULL, kind INTEGER NOT NULL, " +
            "status INTEGER NOT NULL, updated_utc TEXT NOT NULL, payload_json BLOB NOT NULL);" +
            "CREATE INDEX IF NOT EXISTS ix_learning_sessions_lookup ON learning_sessions " +
            "(repository_path COLLATE NOCASE, kind, status, updated_utc DESC);";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async ValueTask EnsureCapacityAsync(
        SqliteConnection connection,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM learning_sessions WHERE session_id = $id;";
        exists.Parameters.AddWithValue("$id", sessionId.ToString("D", CultureInfo.InvariantCulture));
        long existing = (long)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        if (existing > 0)
        {
            return;
        }

        await using SqliteCommand count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM learning_sessions;";
        long total = (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        if (total < _options.MaximumPersistedSessions)
        {
            return;
        }

        int required = checked((int)(total - _options.MaximumPersistedSessions + 1));
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM learning_sessions WHERE session_id IN (" +
            "SELECT session_id FROM learning_sessions WHERE status <> $active " +
            "ORDER BY updated_utc ASC LIMIT $required);";
        command.Parameters.AddWithValue("$active", (int)LearningSessionStatus.Active);
        command.Parameters.AddWithValue("$required", required);
        int removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (removed < required)
        {
            throw new ProjectLearningException("learning_session_capacity");
        }
    }

    private void EnsureMemoryCapacity(Guid sessionId)
    {
        if (_memory.ContainsKey(sessionId) || _memory.Count < _options.MaximumPersistedSessions)
        {
            return;
        }

        ProjectLearningSessionSnapshot? oldestCompleted = _memory.Values
            .Where(static session => session.Status != LearningSessionStatus.Active)
            .OrderBy(static session => session.UpdatedAt)
            .FirstOrDefault();
        if (oldestCompleted is null || !_memory.TryRemove(oldestCompleted.SessionId, out _))
        {
            throw new ProjectLearningException("learning_session_capacity");
        }
    }

    private static ProjectLearningSessionSnapshot Deserialize(byte[] payload)
    {
        if (payload.Length is 0 or > MaximumPayloadBytes)
        {
            throw new ProjectLearningException("learning_session_storage_corrupt");
        }

        try
        {
            return JsonSerializer.Deserialize<ProjectLearningSessionSnapshot>(
                    payload,
                    ProjectLearningJson.Options) ??
                throw new ProjectLearningException("learning_session_storage_corrupt");
        }
        catch (JsonException)
        {
            throw new ProjectLearningException("learning_session_storage_corrupt");
        }
    }
}

internal static class ProjectLearningJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jarvis.Infrastructure.ProjectIntelligence;

internal sealed class SqliteProjectIndexStore : IDisposable
{
    private const int MaximumStoredGitStatusCharacters = 8 * 1024;
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public SqliteProjectIndexStore(Configuration.JarvisDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _databasePath = Configuration.JarvisDataPaths.ResolveUnder(
            paths.ProjectIndexes,
            "project-index.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    public void Dispose() => _initializationLock.Dispose();

    public async ValueTask<RepositoryIndexState?> LoadStateAsync(
        string repositoryId,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand repositoryCommand = connection.CreateCommand();
        repositoryCommand.CommandText =
            "SELECT root_path, snapshot_id, branch, git_status, indexed_at_utc " +
            "FROM repositories WHERE repository_id = $repositoryId;";
        repositoryCommand.Parameters.AddWithValue("$repositoryId", repositoryId);
        await using SqliteDataReader repositoryReader = await repositoryCommand
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await repositoryReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        string rootPath = repositoryReader.GetString(0);
        string snapshotId = repositoryReader.GetString(1);
        string? branch = repositoryReader.IsDBNull(2) ? null : repositoryReader.GetString(2);
        string? gitStatus = repositoryReader.IsDBNull(3) ? null : repositoryReader.GetString(3);
        if (!DateTimeOffset.TryParse(
                repositoryReader.GetString(4),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset indexedAt))
        {
            throw new ProjectIndexException("project_index_storage_corrupt");
        }
        await repositoryReader.DisposeAsync().ConfigureAwait(false);

        await using SqliteCommand fileCommand = connection.CreateCommand();
        fileCommand.CommandText =
            "SELECT relative_path, kind, length, last_write_ticks, content_hash, content " +
            "FROM files WHERE repository_id = $repositoryId;";
        fileCommand.Parameters.AddWithValue("$repositoryId", repositoryId);
        Dictionary<string, StoredFileState> files = new(StringComparer.OrdinalIgnoreCase);
        await using SqliteDataReader fileReader = await fileCommand
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await fileReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            int kind = fileReader.GetInt32(1);
            if (!Enum.IsDefined((IndexedFileKind)kind))
            {
                throw new ProjectIndexException("project_index_storage_corrupt");
            }

            StoredFileState file = new(
                fileReader.GetString(0),
                (IndexedFileKind)kind,
                fileReader.GetInt64(2),
                fileReader.GetInt64(3),
                fileReader.GetString(4),
                fileReader.GetString(5));
            files[file.RelativePath] = file;
        }

        return new RepositoryIndexState(
            repositoryId,
            rootPath,
            snapshotId,
            branch,
            gitStatus,
            indexedAt,
            files);
    }

    public async ValueTask<RepositoryQueryState?> LoadQueryStateAsync(
        string repositoryId,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand repositoryCommand = connection.CreateCommand();
        repositoryCommand.CommandText =
            "SELECT root_path, snapshot_id FROM repositories WHERE repository_id = $repositoryId;";
        repositoryCommand.Parameters.AddWithValue("$repositoryId", repositoryId);
        await using SqliteDataReader repositoryReader = await repositoryCommand
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await repositoryReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        string repositoryPath = repositoryReader.GetString(0);
        string snapshotId = repositoryReader.GetString(1);
        await repositoryReader.DisposeAsync().ConfigureAwait(false);

        await using SqliteCommand fileCommand = connection.CreateCommand();
        fileCommand.CommandText =
            "SELECT relative_path, length, last_write_ticks, content_hash " +
            "FROM files WHERE repository_id = $repositoryId;";
        fileCommand.Parameters.AddWithValue("$repositoryId", repositoryId);
        Dictionary<string, StoredFileMetadata> files = new(StringComparer.OrdinalIgnoreCase);
        await using SqliteDataReader fileReader = await fileCommand
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await fileReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            StoredFileMetadata file = new(
                fileReader.GetString(0),
                fileReader.GetInt64(1),
                fileReader.GetInt64(2),
                fileReader.GetString(3));
            files[file.RelativePath] = file;
        }

        return new RepositoryQueryState(repositoryPath, snapshotId, files);
    }

    public async ValueTask SaveAsync(
        string repositoryId,
        string repositoryPath,
        string snapshotId,
        GitRepositoryMetadata git,
        IReadOnlyList<IndexedFile> files,
        ProjectAnalysisSnapshot analysis,
        DateTimeOffset indexedAtUtc,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await UpsertRepositoryAsync().ConfigureAwait(false);
            await ReplaceRowsAsync("files").ConfigureAwait(false);
            await ReplaceRowsAsync("projects").ConfigureAwait(false);
            await ReplaceRowsAsync("symbols").ConfigureAwait(false);
            await ReplaceRowsAsync("relationships").ConfigureAwait(false);
            await ReplaceRowsAsync("facts").ConfigureAwait(false);
            await ReplaceRowsAsync("search_index").ConfigureAwait(false);
            await InsertFilesAsync().ConfigureAwait(false);
            await InsertProjectsAsync().ConfigureAwait(false);
            await InsertSymbolsAsync().ConfigureAwait(false);
            await InsertRelationshipsAsync().ConfigureAwait(false);
            await InsertFactsAsync().ConfigureAwait(false);
            await InsertSearchRowsAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
            }

            throw;
        }

        async ValueTask UpsertRepositoryAsync()
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO repositories(repository_id, root_path, snapshot_id, branch, git_status, indexed_at_utc) " +
                "VALUES($repositoryId, $rootPath, $snapshotId, $branch, $gitStatus, $indexedAt) " +
                "ON CONFLICT(repository_id) DO UPDATE SET root_path=excluded.root_path, " +
                "snapshot_id=excluded.snapshot_id, branch=excluded.branch, git_status=excluded.git_status, " +
                "indexed_at_utc=excluded.indexed_at_utc;";
            command.Parameters.AddWithValue("$repositoryId", repositoryId);
            command.Parameters.AddWithValue("$rootPath", repositoryPath);
            command.Parameters.AddWithValue("$snapshotId", snapshotId);
            command.Parameters.AddWithValue("$branch", (object?)git.Branch ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$gitStatus",
                git.StatusSummary.Length <= MaximumStoredGitStatusCharacters
                    ? git.StatusSummary
                    : git.StatusSummary[..MaximumStoredGitStatusCharacters]);
            command.Parameters.AddWithValue("$indexedAt", indexedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        async ValueTask ReplaceRowsAsync(string table)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE repository_id = $repositoryId;";
            command.Parameters.AddWithValue("$repositoryId", repositoryId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        async ValueTask InsertFilesAsync()
        {
            foreach (IndexedFile file in files)
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO files(repository_id, relative_path, kind, length, last_write_ticks, content_hash, content) " +
                    "VALUES($repositoryId, $path, $kind, $length, $ticks, $hash, $content);";
                command.Parameters.AddWithValue("$repositoryId", repositoryId);
                command.Parameters.AddWithValue("$path", file.RelativePath);
                command.Parameters.AddWithValue("$kind", (int)file.Kind);
                command.Parameters.AddWithValue("$length", file.Length);
                command.Parameters.AddWithValue("$ticks", file.LastWriteUtcTicks);
                command.Parameters.AddWithValue("$hash", file.ContentHash);
                command.Parameters.AddWithValue("$content", file.Content);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        async ValueTask InsertProjectsAsync()
        {
            foreach (StaticProject project in analysis.Projects)
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO projects(repository_id, relative_path, name, assembly_name, root_namespace, " +
                    "target_frameworks, project_references, package_references, is_test, output_type) " +
                    "VALUES($repositoryId, $path, $name, $assembly, $namespace, $frameworks, $projectRefs, $packageRefs, $isTest, $outputType);";
                command.Parameters.AddWithValue("$repositoryId", repositoryId);
                command.Parameters.AddWithValue("$path", project.RelativePath);
                command.Parameters.AddWithValue("$name", project.Name);
                command.Parameters.AddWithValue("$assembly", project.AssemblyName);
                command.Parameters.AddWithValue("$namespace", project.RootNamespace);
                command.Parameters.AddWithValue("$frameworks", JsonSerializer.Serialize(project.TargetFrameworks));
                command.Parameters.AddWithValue("$projectRefs", JsonSerializer.Serialize(project.ProjectReferences));
                command.Parameters.AddWithValue("$packageRefs", JsonSerializer.Serialize(project.PackageReferences));
                command.Parameters.AddWithValue("$isTest", project.IsTestProject ? 1 : 0);
                command.Parameters.AddWithValue("$outputType", project.OutputType);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        async ValueTask InsertSymbolsAsync()
        {
            foreach (IndexedSymbol symbol in analysis.Symbols)
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO symbols(repository_id, symbol_id, relative_path, project_path, kind, name, qualified_name, namespace, " +
                    "start_line, end_line, declaration, content_hash) VALUES($repositoryId, $id, $path, $project, $kind, $name, " +
                    "$qualified, $namespace, $start, $end, $declaration, $hash);";
                command.Parameters.AddWithValue("$repositoryId", repositoryId);
                command.Parameters.AddWithValue("$id", symbol.Id);
                command.Parameters.AddWithValue("$path", symbol.RelativePath);
                command.Parameters.AddWithValue("$project", (object?)symbol.ProjectPath ?? DBNull.Value);
                command.Parameters.AddWithValue("$kind", (int)symbol.Kind);
                command.Parameters.AddWithValue("$name", symbol.Name);
                command.Parameters.AddWithValue("$qualified", symbol.QualifiedName);
                command.Parameters.AddWithValue("$namespace", symbol.Namespace);
                command.Parameters.AddWithValue("$start", symbol.StartLine);
                command.Parameters.AddWithValue("$end", symbol.EndLine);
                command.Parameters.AddWithValue("$declaration", symbol.Declaration);
                command.Parameters.AddWithValue("$hash", symbol.ContentHash);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        async ValueTask InsertRelationshipsAsync()
        {
            foreach (IndexedRelationship relationship in analysis.Relationships)
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO relationships(repository_id, source_symbol_id, target_symbol_id, source_name, target_name, kind, " +
                    "relative_path, start_line, end_line, content_hash) VALUES($repositoryId, $sourceId, $targetId, $sourceName, " +
                    "$targetName, $kind, $path, $start, $end, $hash);";
                command.Parameters.AddWithValue("$repositoryId", repositoryId);
                command.Parameters.AddWithValue("$sourceId", (object?)relationship.SourceSymbolId ?? DBNull.Value);
                command.Parameters.AddWithValue("$targetId", (object?)relationship.TargetSymbolId ?? DBNull.Value);
                command.Parameters.AddWithValue("$sourceName", relationship.SourceName);
                command.Parameters.AddWithValue("$targetName", relationship.TargetName);
                command.Parameters.AddWithValue("$kind", (int)relationship.Kind);
                command.Parameters.AddWithValue("$path", relationship.RelativePath);
                command.Parameters.AddWithValue("$start", relationship.StartLine);
                command.Parameters.AddWithValue("$end", relationship.EndLine);
                command.Parameters.AddWithValue("$hash", relationship.ContentHash);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        async ValueTask InsertFactsAsync()
        {
            foreach (IndexedFact fact in analysis.Facts)
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO facts(repository_id, kind, name, detail, relative_path, start_line, end_line, symbol, excerpt, content_hash) " +
                    "VALUES($repositoryId, $kind, $name, $detail, $path, $start, $end, $symbol, $excerpt, $hash);";
                command.Parameters.AddWithValue("$repositoryId", repositoryId);
                command.Parameters.AddWithValue("$kind", fact.Kind);
                command.Parameters.AddWithValue("$name", fact.Name);
                command.Parameters.AddWithValue("$detail", fact.Detail);
                command.Parameters.AddWithValue("$path", fact.RelativePath);
                command.Parameters.AddWithValue("$start", fact.StartLine);
                command.Parameters.AddWithValue("$end", fact.EndLine);
                command.Parameters.AddWithValue("$symbol", (object?)fact.Symbol ?? DBNull.Value);
                command.Parameters.AddWithValue("$excerpt", fact.Excerpt);
                command.Parameters.AddWithValue("$hash", fact.ContentHash);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        async ValueTask InsertSearchRowsAsync()
        {
            foreach (IndexedFile file in files)
            {
                await InsertSearchRowAsync(
                    "file",
                    file.RelativePath,
                    file.RelativePath,
                    null,
                    1,
                    Math.Max(1, CountLines(file.Content)),
                    file.Content,
                    file.ContentHash).ConfigureAwait(false);
            }

            foreach (IndexedSymbol symbol in analysis.Symbols)
            {
                await InsertSearchRowAsync(
                    "symbol",
                    symbol.Id,
                    symbol.RelativePath,
                    symbol.QualifiedName,
                    symbol.StartLine,
                    symbol.EndLine,
                    symbol.Declaration,
                    symbol.ContentHash).ConfigureAwait(false);
            }

            foreach (IndexedFact fact in analysis.Facts)
            {
                await InsertSearchRowAsync(
                    "fact",
                    $"{fact.Kind}|{fact.Name}|{fact.RelativePath}|{fact.StartLine}",
                    fact.RelativePath,
                    fact.Symbol,
                    fact.StartLine,
                    fact.EndLine,
                    $"{fact.Kind} {fact.Name} {fact.Detail} {fact.Excerpt}",
                    fact.ContentHash).ConfigureAwait(false);
            }
        }

        async ValueTask InsertSearchRowAsync(
            string sourceKind,
            string sourceId,
            string path,
            string? symbol,
            int startLine,
            int endLine,
            string content,
            string hash)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO search_index(repository_id, source_kind, source_id, relative_path, symbol, start_line, end_line, content, content_hash) " +
                "VALUES($repositoryId, $sourceKind, $sourceId, $path, $symbol, $start, $end, $content, $hash);";
            command.Parameters.AddWithValue("$repositoryId", repositoryId);
            command.Parameters.AddWithValue("$sourceKind", sourceKind);
            command.Parameters.AddWithValue("$sourceId", sourceId);
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$symbol", (object?)symbol ?? DBNull.Value);
            command.Parameters.AddWithValue("$start", startLine);
            command.Parameters.AddWithValue("$end", endLine);
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$hash", hash);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask UpdateGitMetadataAsync(
        string repositoryId,
        GitRepositoryMetadata git,
        DateTimeOffset indexedAtUtc,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE repositories SET branch = $branch, git_status = $status, indexed_at_utc = $indexedAt " +
            "WHERE repository_id = $repositoryId;";
        command.Parameters.AddWithValue("$repositoryId", repositoryId);
        command.Parameters.AddWithValue("$branch", (object?)git.Branch ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$status",
            git.StatusSummary.Length <= MaximumStoredGitStatusCharacters
                ? git.StatusSummary
                : git.StatusSummary[..MaximumStoredGitStatusCharacters]);
        command.Parameters.AddWithValue("$indexedAt", indexedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ProjectSearchRow>> SearchAsync(
        string repositoryId,
        string query,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        string match = CreateFtsQuery(query);
        if (match.Length == 0)
        {
            return [];
        }

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT source_kind, source_id, relative_path, symbol, start_line, end_line, content, content_hash, bm25(search_index) " +
            "FROM search_index WHERE repository_id = $repositoryId AND search_index MATCH $query " +
            "ORDER BY bm25(search_index) LIMIT $limit;";
        command.Parameters.AddWithValue("$repositoryId", repositoryId);
        command.Parameters.AddWithValue("$query", match);
        command.Parameters.AddWithValue("$limit", maximumResults);
        List<ProjectSearchRow> rows = [];
        try
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ProjectSearchRow row = new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetDouble(8));
                rows.Add(row.SourceKind == "file" ? LocateFileEvidence(row, query) : row);
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1)
        {
            throw new ProjectIndexException("project_search_query_invalid");
        }

        return rows;
    }

    public ValueTask<IReadOnlyList<ProjectSearchRow>> FindSymbolsAsync(
        string repositoryId,
        string symbol,
        int maximumResults,
        CancellationToken cancellationToken) => QueryRowsAsync(
            "SELECT 'symbol', symbol_id, relative_path, qualified_name, start_line, end_line, declaration, content_hash, 0.0 " +
            "FROM symbols WHERE repository_id = $repositoryId AND (name = $value COLLATE NOCASE OR qualified_name LIKE $like ESCAPE '\\') " +
            "ORDER BY CASE WHEN name = $value COLLATE NOCASE THEN 0 ELSE 1 END, qualified_name LIMIT $limit;",
            repositoryId,
            symbol,
            maximumResults,
            cancellationToken);

    public ValueTask<IReadOnlyList<ProjectSearchRow>> FindFactsAsync(
        string repositoryId,
        string kind,
        int maximumResults,
        CancellationToken cancellationToken) => QueryRowsAsync(
            "SELECT 'fact', kind || '|' || name || '|' || relative_path || '|' || start_line, relative_path, symbol, " +
            "start_line, end_line, detail || char(10) || excerpt, content_hash, 0.0 FROM facts " +
            "WHERE repository_id = $repositoryId AND kind = $value COLLATE NOCASE ORDER BY relative_path, start_line LIMIT $limit;",
            repositoryId,
            kind,
            maximumResults,
            cancellationToken);

    public ValueTask<IReadOnlyList<ProjectSearchRow>> FindRelationshipsAsync(
        string repositoryId,
        string symbol,
        int maximumResults,
        CancellationToken cancellationToken) => QueryRowsAsync(
            "SELECT 'relationship', coalesce(source_symbol_id, '') || '|' || coalesce(target_symbol_id, ''), relative_path, " +
            "CASE WHEN source_name LIKE $like ESCAPE '\\' THEN target_name ELSE source_name END, start_line, end_line, " +
            "source_name || ' ' || CASE kind WHEN 0 THEN 'inherits' WHEN 1 THEN 'implements' WHEN 2 THEN 'calls' ELSE 'references' END || ' ' || target_name, " +
            "content_hash, 0.0 FROM relationships " +
            "WHERE repository_id = $repositoryId AND (source_name LIKE $like ESCAPE '\\' OR target_name LIKE $like ESCAPE '\\') " +
            "ORDER BY relative_path, start_line LIMIT $limit;",
            repositoryId,
            symbol,
            maximumResults,
            cancellationToken);

    public async ValueTask<(int Projects, int Sources, int Symbols, int Relationships)> GetCountsAsync(
        string repositoryId,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int projects = await ScalarAsync("projects").ConfigureAwait(false);
        int sources = await ScalarAsync("files", " AND kind = 0").ConfigureAwait(false);
        int symbols = await ScalarAsync("symbols").ConfigureAwait(false);
        int relationships = await ScalarAsync("relationships").ConfigureAwait(false);
        return (projects, sources, symbols, relationships);

        async ValueTask<int> ScalarAsync(string table, string suffix = "")
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM {table} WHERE repository_id = $repositoryId{suffix};";
            command.Parameters.AddWithValue("$repositoryId", repositoryId);
            return Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }
    }

    private async ValueTask<IReadOnlyList<ProjectSearchRow>> QueryRowsAsync(
        string sql,
        string repositoryId,
        string value,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$repositoryId", repositoryId);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$like", "%" + EscapeLike(value) + "%");
        command.Parameters.AddWithValue("$limit", maximumResults);
        List<ProjectSearchRow> rows = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ProjectSearchRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetDouble(8)));
        }

        return rows;
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = Schema;
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 1)
            {
                throw new ProjectIndexException("sqlite_fts5_unavailable");
            }

            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string CreateFtsQuery(string query)
    {
        List<string> tokens = [];
        StringBuilder current = new();
        foreach (char character in query)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                if (current.Length < 64)
                {
                    current.Append(character);
                }
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
                if (tokens.Count == 8)
                {
                    break;
                }
            }
        }

        if (current.Length > 0 && tokens.Count < 8)
        {
            tokens.Add(current.ToString());
        }

        return string.Join(" AND ", tokens.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static token => $"\"{token}\"*"));
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static ProjectSearchRow LocateFileEvidence(ProjectSearchRow row, string query)
    {
        string[] tokens = query.Split(
            [.. query.Where(static character => !char.IsLetterOrDigit(character) && character != '_').Distinct()],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] lines = row.Excerpt.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int matched = Array.FindIndex(lines, line =>
            tokens.Any(token => line.Contains(token, StringComparison.OrdinalIgnoreCase)));
        matched = Math.Max(0, matched);
        int start = matched;
        int end = Math.Min(lines.Length - 1, matched + 3);
        return row with
        {
            StartLine = start + 1,
            EndLine = end + 1,
            Excerpt = string.Join('\n', lines[start..(end + 1)]),
        };
    }

    private static int CountLines(string content) =>
        content.Length == 0 ? 1 : content.Count(static character => character == '\n') + 1;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS repositories(
            repository_id TEXT PRIMARY KEY,
            root_path TEXT NOT NULL,
            snapshot_id TEXT NOT NULL,
            branch TEXT NULL,
            git_status TEXT NULL,
            indexed_at_utc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS files(
            repository_id TEXT NOT NULL,
            relative_path TEXT NOT NULL,
            kind INTEGER NOT NULL,
            length INTEGER NOT NULL,
            last_write_ticks INTEGER NOT NULL,
            content_hash TEXT NOT NULL,
            content TEXT NOT NULL,
            PRIMARY KEY(repository_id, relative_path),
            FOREIGN KEY(repository_id) REFERENCES repositories(repository_id) ON DELETE CASCADE);
        CREATE TABLE IF NOT EXISTS projects(
            repository_id TEXT NOT NULL,
            relative_path TEXT NOT NULL,
            name TEXT NOT NULL,
            assembly_name TEXT NOT NULL,
            root_namespace TEXT NOT NULL,
            target_frameworks TEXT NOT NULL,
            project_references TEXT NOT NULL,
            package_references TEXT NOT NULL,
            is_test INTEGER NOT NULL,
            output_type TEXT NOT NULL,
            PRIMARY KEY(repository_id, relative_path));
        CREATE TABLE IF NOT EXISTS symbols(
            repository_id TEXT NOT NULL,
            symbol_id TEXT NOT NULL,
            relative_path TEXT NOT NULL,
            project_path TEXT NULL,
            kind INTEGER NOT NULL,
            name TEXT NOT NULL,
            qualified_name TEXT NOT NULL,
            namespace TEXT NOT NULL,
            start_line INTEGER NOT NULL,
            end_line INTEGER NOT NULL,
            declaration TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            PRIMARY KEY(repository_id, symbol_id));
        CREATE INDEX IF NOT EXISTS ix_symbols_name ON symbols(repository_id, name);
        CREATE TABLE IF NOT EXISTS relationships(
            repository_id TEXT NOT NULL,
            source_symbol_id TEXT NULL,
            target_symbol_id TEXT NULL,
            source_name TEXT NOT NULL,
            target_name TEXT NOT NULL,
            kind INTEGER NOT NULL,
            relative_path TEXT NOT NULL,
            start_line INTEGER NOT NULL,
            end_line INTEGER NOT NULL,
            content_hash TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_relationships_source ON relationships(repository_id, source_name);
        CREATE INDEX IF NOT EXISTS ix_relationships_target ON relationships(repository_id, target_name);
        CREATE TABLE IF NOT EXISTS facts(
            repository_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            name TEXT NOT NULL,
            detail TEXT NOT NULL,
            relative_path TEXT NOT NULL,
            start_line INTEGER NOT NULL,
            end_line INTEGER NOT NULL,
            symbol TEXT NULL,
            excerpt TEXT NOT NULL,
            content_hash TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_facts_kind ON facts(repository_id, kind);
        CREATE VIRTUAL TABLE IF NOT EXISTS search_index USING fts5(
            repository_id UNINDEXED,
            source_kind UNINDEXED,
            source_id UNINDEXED,
            relative_path UNINDEXED,
            symbol,
            start_line UNINDEXED,
            end_line UNINDEXED,
            content,
            content_hash UNINDEXED,
            tokenize='unicode61');
        """;
}

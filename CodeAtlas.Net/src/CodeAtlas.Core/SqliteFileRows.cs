using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodeAtlas.Storage;

internal sealed partial class SqliteIndexWriter
{
    /// <summary>Test seam. Throws inside the replacement transaction, before commit.</summary>
    internal static Action? BeforeFileRowCommit { get; set; }

    /// <summary>
    /// Replaces one file's method and reference rows in a private working copy.
    /// Published generations are not opened here. The file hash, manifest and generation header stay as copied.
    /// </summary>
    public static void ReplaceFileRows(string path, string relativeFile, MethodInfo[] methods,
        IReadOnlyDictionary<string, SourceReference[]> references)
    {
        RequireFileRows(relativeFile, methods, references);
        SqliteAvailability.Require();
        using var connection = OpenWorkingCopy(path);
        Execute(connection, "PRAGMA journal_mode=DELETE;");
        using var transaction = connection.BeginTransaction();
        try
        {
            ApplyFileRows(connection, transaction, relativeFile, methods, references);
            BeforeFileRowCommit?.Invoke();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Replaces one file's method, reference, type, symbol, fact, search and duplicate rows,
    /// and records the applied text hash and incremental tree hash. One transaction: a failure leaves the copy unchanged.
    /// </summary>
    internal static void UpdateWorkingCopy(string database, string absolutePath, string relativeFile, string text,
        MethodInfo[] methods, IReadOnlyDictionary<string, SourceReference[]> references,
        DocumentedSymbol[] symbols, StructuralCatalog catalog, DuplicateCatalog duplicates,
        IndexedType[] types, string contentHash, string? surfaceHash)
    {
        RequireFileRows(relativeFile, methods, references);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(duplicates);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        SqliteAvailability.Require();
        using var connection = OpenWorkingCopy(database);
        Execute(connection, "PRAGMA journal_mode=DELETE;");
        using var transaction = connection.BeginTransaction();
        try
        {
            ApplyFileRows(connection, transaction, relativeFile, methods, references);
            ReplaceCatalog(connection, transaction, symbols, catalog, duplicates);
            WriteTypeRows(connection, transaction, types);
            UpdateTreeHash(connection, transaction, relativeFile, contentHash, surfaceHash);
            RecordAppliedHash(connection, transaction, absolutePath, relativeFile, text);
            BeforeFileRowCommit?.Invoke();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>Gives a generation copy a new id. Tracked file hashes stay as recorded for the text that was indexed.</summary>
    internal static void PreparePublishedCopy(string path, string indexId)
    {
        using var connection = OpenWorkingCopy(path);
        Execute(connection, "PRAGMA journal_mode=DELETE;");
        using var transaction = connection.BeginTransaction();
        SetGenerationId(connection, transaction, indexId);
        SetDuplicateGeneration(connection, transaction, indexId);
        transaction.Commit();
    }

    private static void WriteTypeRows(SqliteConnection connection, SqliteTransaction transaction, IndexedType[] types)
    {
        Execute("DELETE FROM types", connection, transaction);
        var files = ReadFileIds(connection, transaction);
        for (var i = 0; i < types.Length; i++)
        {
            var type = types[i];
            Execute("INSERT INTO types VALUES($o,$id,$name,$kind,$project,$file,$line,$col)", connection, transaction,
                ("$o", i), ("$id", type.Id), ("$name", type.Name), ("$kind", type.Kind), ("$project", type.Project),
                ("$file", FileId(files, type.Location.File)), ("$line", type.Location.Line), ("$col", type.Location.Column));
        }
    }

    private static void UpdateTreeHash(SqliteConnection connection, SqliteTransaction transaction,
        string relativeFile, string contentHash, string? surfaceHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE incremental_trees SET content_hash = $content, surface_hash = $surface WHERE origin_path = $path";
        command.Parameters.AddWithValue("$content", contentHash);
        command.Parameters.AddWithValue("$surface", Db(surfaceHash));
        command.Parameters.AddWithValue("$path", relativeFile);
        if (command.ExecuteNonQuery() == 0)
            throw new IndexFormatException("invalid_index", "The working copy has no incremental tree for " + relativeFile + ".");
    }

    private static void ReplaceCatalog(SqliteConnection connection, SqliteTransaction transaction,
        DocumentedSymbol[] symbols, StructuralCatalog catalog, DuplicateCatalog duplicates)
    {
        ClearCatalog(connection, transaction);
        var files = ReadFileIds(connection, transaction);
        WriteSymbolRows(connection, transaction, symbols, files);
        WriteFactRows(connection, transaction, catalog, files);
        WriteSearchRows(connection, transaction, symbols, catalog);
        WriteDuplicateRows(connection, transaction, duplicates, files);
        WriteDuplicateHeader(connection, transaction, duplicates);
    }

    private static void ClearCatalog(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var table in new[]
        {
            "documentation", "search_postings", "search_tokens", "search_field_lengths", "symbols", "facts",
            "structural_issues", "duplicate_groups", "duplicate_vocabulary", "duplicate_bodies",
            "duplicate_body_omissions", "duplicate_candidates", "duplicate_scan_omissions"
        })
            Execute("DELETE FROM " + table, connection, transaction);
    }

    private static void WriteSymbolRows(SqliteConnection connection, SqliteTransaction transaction,
        DocumentedSymbol[] symbols, Dictionary<string, long> files)
    {
        for (var i = 0; i < symbols.Length; i++)
        {
            var symbol = symbols[i];
            Execute("INSERT INTO symbols VALUES($o,$id,$name,$kind,$tk,$sig,$project,$ns,$type,$file,$line,$col,$locations)",
                connection, transaction,
                ("$o", i), ("$id", symbol.Id), ("$name", symbol.Name), ("$kind", symbol.Kind), ("$tk", Db(symbol.TypeKind)),
                ("$sig", symbol.Signature), ("$project", symbol.Project), ("$ns", symbol.Namespace), ("$type", Db(symbol.ContainingType)),
                ("$file", FileId(files, symbol.Location.File)), ("$line", symbol.Location.Line), ("$col", symbol.Location.Column),
                ("$locations", Json(symbol.Locations)));
            Execute("INSERT INTO documentation VALUES($o,$status,$value)", connection, transaction,
                ("$o", i), ("$status", symbol.Documentation.Status), ("$value", Json(symbol.Documentation)));
        }
    }

    private static void WriteFactRows(SqliteConnection connection, SqliteTransaction transaction,
        StructuralCatalog catalog, Dictionary<string, long> files)
    {
        for (var i = 0; i < catalog.Facts.Length; i++)
        {
            var fact = catalog.Facts[i];
            var target = fact.Target;
            var range = fact.Evidence.Location;
            Execute("INSERT INTO facts VALUES($o,$owner,$kind,$tn,$ts,$td,$tsid,$ta,$tr,$origin,$file,$sl,$sc,$efile,$el,$ec,$scope)",
                connection, transaction,
                ("$o", i), ("$owner", fact.OwnerId), ("$kind", fact.Kind), ("$tn", target.Name), ("$ts", target.Signature),
                ("$td", Db(target.DeclarationId)), ("$tsid", Db(target.SourceSymbolId)), ("$ta", Db(target.AssemblyIdentity)),
                ("$tr", target.Resolution), ("$origin", fact.Evidence.Origin),
                ("$file", FileId(files, range.Start.File)), ("$sl", range.Start.Line), ("$sc", range.Start.Column),
                ("$efile", FileId(files, range.End.File)), ("$el", range.End.Line), ("$ec", range.End.Column),
                ("$scope", fact.Evidence.SemanticScope));
        }
        for (var i = 0; i < catalog.Issues.Length; i++)
        {
            var issue = catalog.Issues[i];
            Execute("INSERT INTO structural_issues VALUES($o,$owner,$code,$file,$value)", connection, transaction,
                ("$o", i), ("$owner", issue.OwnerId), ("$code", issue.Code),
                ("$file", FileId(files, issue.Location.Start.File)), ("$value", Json(issue)));
        }
    }

    private static void WriteSearchRows(SqliteConnection connection, SqliteTransaction transaction,
        DocumentedSymbol[] symbols, StructuralCatalog catalog)
    {
        var tokenIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var totals = PurposeSearch.BuildStoreIndex(symbols, catalog, (ordinal, tokens) =>
        {
            foreach (var (token, nameField) in tokens)
            {
                if (!tokenIds.TryGetValue(token, out var id))
                {
                    tokenIds.Add(token, id = tokenIds.Count + 1);
                    Execute("INSERT INTO search_tokens VALUES($id,$token)", connection, transaction, ("$id", id), ("$token", token));
                }
                Execute("INSERT INTO search_postings VALUES($token,$symbol,$name)", connection, transaction,
                    ("$token", id), ("$symbol", ordinal), ("$name", nameField ? 1 : 0));
            }
        });
        foreach (var (kind, families) in totals)
            foreach (var (family, total) in families)
                Execute("INSERT INTO search_field_lengths VALUES($kind,$family,$total,$count)", connection, transaction,
                    ("$kind", kind), ("$family", family), ("$total", total.Length), ("$count", total.Count));
    }

    private static void WriteDuplicateRows(SqliteConnection connection, SqliteTransaction transaction,
        DuplicateCatalog duplicates, Dictionary<string, long> files)
    {
        if (duplicates.Analysis is not { } analysis || duplicates.Scan is not { } scan)
            throw new ArgumentException("Replacement duplicates must include analysis and a scan.", nameof(duplicates));
        for (var i = 0; i < duplicates.Groups.Length; i++)
            Execute("INSERT INTO duplicate_groups VALUES($o,$value)", connection, transaction, ("$o", i), ("$value", Json(duplicates.Groups[i])));
        for (var i = 0; i < analysis.Vocabulary.Length; i++)
            Execute("INSERT INTO duplicate_vocabulary VALUES($o,$token)", connection, transaction, ("$o", i), ("$token", analysis.Vocabulary[i]));
        WriteDuplicateBodies(connection, transaction, analysis, files);
        for (var i = 0; i < analysis.Omissions.Length; i++)
            Execute("INSERT INTO duplicate_body_omissions VALUES($o,$symbol,$value)", connection, transaction,
                ("$o", i), ("$symbol", analysis.Omissions[i].SymbolId), ("$value", Json(analysis.Omissions[i])));
        for (var i = 0; i < scan.Candidates.Length; i++)
        {
            var candidate = scan.Candidates[i];
            Execute("INSERT INTO duplicate_candidates VALUES($o,$left,$right,$lp,$rp,$value)", connection, transaction,
                ("$o", i), ("$left", candidate.Left.SymbolId), ("$right", candidate.Right.SymbolId),
                ("$lp", candidate.Left.Project), ("$rp", candidate.Right.Project), ("$value", Json(candidate)));
        }
        for (var i = 0; i < scan.Omissions.Length; i++)
            Execute("INSERT INTO duplicate_scan_omissions VALUES($o,$symbol,$value)", connection, transaction,
                ("$o", i), ("$symbol", scan.Omissions[i].SymbolId), ("$value", Json(scan.Omissions[i])));
    }

    private static void WriteDuplicateBodies(SqliteConnection connection, SqliteTransaction transaction,
        DuplicateBodyCatalog analysis, Dictionary<string, long> files)
    {
        for (var i = 0; i < analysis.Bodies.Length; i++)
        {
            var body = analysis.Bodies[i];
            Execute("INSERT INTO duplicate_bodies VALUES($o,$symbol,$file,$count,$value)", connection, transaction,
                ("$o", i), ("$symbol", body.SymbolId), ("$file", FileId(files, body.Location.Start.File)),
                ("$count", body.Tokens.Length), ("$value", Json(body)));
        }
    }

    private static void WriteDuplicateHeader(SqliteConnection connection, SqliteTransaction transaction, DuplicateCatalog duplicates)
    {
        var indexId = JsonSerializer.Deserialize<string>(RequiredMeta(connection, transaction, "indexId"))
            ?? throw new IndexFormatException("invalid_index", "The working copy has no generation id.");
        var header = duplicates with
        {
            Groups = [],
            Analysis = duplicates.Analysis is { } analysis ? analysis with { Vocabulary = [], Bodies = [], Omissions = [] } : null,
            Scan = duplicates.Scan is { } scan ? scan with { IndexId = indexId, Candidates = [], Omissions = [] } : null
        };
        Execute("UPDATE meta SET value = $value WHERE key = 'duplicates'", connection, transaction, ("$value", Json(header)));
    }

    private static void RecordAppliedHash(SqliteConnection connection, SqliteTransaction transaction,
        string absolutePath, string relativeFile, string text)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(text)));
        var recorded = false;
        foreach (var (path, _) in ReadInputHashes(connection, transaction))
        {
            if (!SnapshotInputModel.PathComparer.Equals(path, absolutePath)) continue;
            Execute("UPDATE input_files SET sha256 = $hash, present = 1 WHERE path = $path", connection, transaction, ("$hash", hash), ("$path", path));
            Execute("UPDATE fingerprints SET sha256 = $hash WHERE path = $path", connection, transaction, ("$hash", hash), ("$path", path));
            recorded = true;
        }
        if (!recorded)
            throw new IndexFormatException("invalid_index", "The working copy has no input row for " + absolutePath + ".");
        Execute("UPDATE files SET content_hash = $hash WHERE path = $path", connection, transaction, ("$hash", hash), ("$path", relativeFile));
    }

    private static void SetDuplicateGeneration(SqliteConnection connection, SqliteTransaction transaction, string indexId)
    {
        var catalog = JsonSerializer.Deserialize<DuplicateCatalog>(RequiredMeta(connection, transaction, "duplicates"), SqliteIndexSchema.Json);
        if (catalog?.Scan is not { } scan || scan.IndexId == indexId) return;
        Execute("UPDATE meta SET value = $value WHERE key = 'duplicates'", connection, transaction,
            ("$value", Json(catalog with { Scan = scan with { IndexId = indexId } })));
    }

    private static string RequiredMeta(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string
            ?? throw new IndexFormatException("invalid_index", "The working copy has no " + key + " record.");
    }

    private static long FileId(Dictionary<string, long> files, string path) =>
        files.TryGetValue(path, out var id) ? id
            : throw new ArgumentException("The working copy has no file row for " + path + ".", nameof(path));

    private static object Db(object? value) => value ?? DBNull.Value;

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, SqliteIndexSchema.Json);

    private static void SetGenerationId(SqliteConnection connection, SqliteTransaction transaction, string indexId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE meta SET value = $value WHERE key = 'indexId'";
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(indexId, SqliteIndexSchema.Json));
        if (command.ExecuteNonQuery() != 1)
            throw new IndexFormatException("invalid_index", "The working copy has no generation id.");
    }

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static List<(string Path, string? Hash)> ReadInputHashes(SqliteConnection connection, SqliteTransaction transaction)
    {
        var result = new List<(string, string?)>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT path, sha256 FROM input_files";
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return result;
    }

    private static void RequireFileRows(string relativeFile, MethodInfo[] methods,
        IReadOnlyDictionary<string, SourceReference[]> references)
    {
        ArgumentNullException.ThrowIfNull(methods);
        ArgumentNullException.ThrowIfNull(references);
        if (string.IsNullOrWhiteSpace(relativeFile))
            throw new ArgumentException("A solution-relative file path is required.", nameof(relativeFile));
        if (methods.Select(method => method.Id).Distinct(StringComparer.Ordinal).Count() != methods.Length)
            throw new ArgumentException("Replacement methods must have distinct ids.", nameof(methods));
        foreach (var method in methods)
            if (!string.Equals(method.Location.File, relativeFile, StringComparison.Ordinal))
                throw new ArgumentException("Replacement methods must be located in " + relativeFile + ".", nameof(methods));
        foreach (var reference in references.SelectMany(pair => pair.Value))
            if (!string.Equals(reference.Location.File, relativeFile, StringComparison.Ordinal))
                throw new ArgumentException("Replacement references must be located in " + relativeFile + ".", nameof(references));
    }

    private static SqliteConnection OpenWorkingCopy(string path)
    {
        if (!File.Exists(path))
            throw new IOException("The working copy database does not exist: " + path);
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void ApplyFileRows(SqliteConnection connection, SqliteTransaction transaction, string relativeFile,
        MethodInfo[] methods, IReadOnlyDictionary<string, SourceReference[]> references)
    {
        var fileId = RequiredFileId(connection, transaction, relativeFile);
        var existing = ReadMethodRows(connection, transaction);
        var removed = existing.Where(row => row.FileId == fileId).Select(row => row.Id)
            .Except(methods.Select(method => method.Id), StringComparer.Ordinal).ToArray();
        if (methods.Any(method => existing.Any(row => row.FileId != fileId && row.Id == method.Id)))
            throw new ArgumentException("A replacement method id already belongs to another file.", nameof(methods));
        Execute("DELETE FROM source_references WHERE file_id = $file", connection, transaction, ("$file", fileId));
        Execute("DELETE FROM methods WHERE file_id = $file", connection, transaction, ("$file", fileId));
        foreach (var methodId in removed)
            DeleteReferenceKey(connection, transaction, methodId);

        var kept = existing.Where(row => row.FileId != fileId).OrderBy(row => row.Ordinal).Select(row => row.Id).ToArray();
        var ordinals = OrdinalMap(MergeIds(kept, methods.Select(method => method.Id).ToArray()));
        MoveMethodOrdinals(connection, transaction, existing.Where(row => row.FileId != fileId), ordinals);
        using (var insert = Prepare(connection, transaction, InsertMethodSql))
            foreach (var method in methods)
                InsertMethod(insert, ordinals[method.Id], method, fileId);

        var keys = ReadKeys(connection, transaction);
        MoveKeyOrdinals(connection, transaction, keys, ordinals);
        using (var insert = Prepare(connection, transaction, InsertReferenceKeySql))
            foreach (var method in methods)
                if (!keys.ContainsKey(method.Id))
                    InsertReferenceKey(insert, ordinals[method.Id], method.Id);
        InsertFileReferences(connection, transaction, fileId, ordinals, references, ReadFileIds(connection, transaction));
    }

    private static long RequiredFileId(SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        var id = Scalar(connection, transaction, "SELECT id FROM files WHERE path = $path", ("$path", path));
        if (id is null)
            throw new ArgumentException("The working copy has no file row for " + path + ".", nameof(path));
        return id.Value;
    }

    private readonly record struct MethodRow(long Ordinal, string Id, long FileId);

    private static List<MethodRow> ReadMethodRows(SqliteConnection connection, SqliteTransaction transaction)
    {
        var result = new List<MethodRow>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ordinal, id, file_id FROM methods ORDER BY ordinal";
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
        return result;
    }

    private static Dictionary<string, long> ReadKeys(SqliteConnection connection, SqliteTransaction transaction)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT method_id, ordinal FROM reference_keys";
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0), reader.GetInt64(1));
        return result;
    }

    private static Dictionary<string, long> ReadFileIds(SqliteConnection connection, SqliteTransaction transaction)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT path, id FROM files";
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0), reader.GetInt64(1));
        return result;
    }

    private static void DeleteReferenceKey(SqliteConnection connection, SqliteTransaction transaction, string methodId)
    {
        var ordinal = Scalar(connection, transaction, "SELECT ordinal FROM reference_keys WHERE method_id = $id", ("$id", methodId));
        if (ordinal is null) return;
        Execute("DELETE FROM source_references WHERE key_ordinal = $key", connection, transaction, ("$key", ordinal.Value));
        Execute("DELETE FROM reference_keys WHERE method_id = $id", connection, transaction, ("$id", methodId));
    }

    private static string[] MergeIds(IReadOnlyList<string> kept, IReadOnlyList<string> replacements)
    {
        var merged = new List<string>(kept.Count + replacements.Count);
        var i = 0;
        var j = 0;
        while (i < kept.Count && j < replacements.Count)
            merged.Add(string.CompareOrdinal(kept[i], replacements[j]) <= 0 ? kept[i++] : replacements[j++]);
        for (; i < kept.Count; i++) merged.Add(kept[i]);
        for (; j < replacements.Count; j++) merged.Add(replacements[j]);
        return merged.ToArray();
    }

    private static Dictionary<string, long> OrdinalMap(IReadOnlyList<string> ids)
    {
        var map = new Dictionary<string, long>(ids.Count, StringComparer.Ordinal);
        for (var i = 0; i < ids.Count; i++) map.Add(ids[i], i);
        return map;
    }

    private static void MoveMethodOrdinals(SqliteConnection connection, SqliteTransaction transaction,
        IEnumerable<MethodRow> kept, Dictionary<string, long> ordinals)
    {
        var changing = kept.Where(row => row.Ordinal != ordinals[row.Id]).ToArray();
        for (var i = 0; i < changing.Length; i++)
            Execute("UPDATE methods SET ordinal = $ordinal WHERE id = $id", connection, transaction,
                ("$ordinal", (long)-i - 1), ("$id", changing[i].Id));
        foreach (var row in changing)
            Execute("UPDATE methods SET ordinal = $ordinal WHERE id = $id", connection, transaction,
                ("$ordinal", ordinals[row.Id]), ("$id", row.Id));
    }

    private static void MoveKeyOrdinals(SqliteConnection connection, SqliteTransaction transaction,
        Dictionary<string, long> keys, Dictionary<string, long> ordinals)
    {
        var changing = keys.Where(pair => pair.Value != ordinals[pair.Key]).Select(pair => (pair.Key, From: pair.Value)).ToArray();
        var parked = new long[changing.Length];
        for (var i = 0; i < changing.Length; i++)
        {
            parked[i] = -i - 1;
            SetKeyOrdinal(connection, transaction, changing[i].Key, changing[i].From, parked[i]);
        }
        for (var i = 0; i < changing.Length; i++)
            SetKeyOrdinal(connection, transaction, changing[i].Key, parked[i], ordinals[changing[i].Key]);
    }

    private static void SetKeyOrdinal(SqliteConnection connection, SqliteTransaction transaction, string methodId, long from, long to)
    {
        Execute("UPDATE reference_keys SET ordinal = $ordinal WHERE method_id = $id", connection, transaction,
            ("$ordinal", to), ("$id", methodId));
        Execute("UPDATE source_references SET key_ordinal = $to WHERE key_ordinal = $from", connection, transaction,
            ("$to", to), ("$from", from));
    }

    private static void InsertFileReferences(SqliteConnection connection, SqliteTransaction transaction, long fileId,
        Dictionary<string, long> ordinals, IReadOnlyDictionary<string, SourceReference[]> references, Dictionary<string, long> fileIds)
    {
        using var insert = Prepare(connection, transaction, InsertSourceReferenceSql);
        foreach (var (methodId, rows) in references)
        {
            if (rows.Length == 0) continue;
            if (!ordinals.TryGetValue(methodId, out var key))
                throw new ArgumentException("Replacement references target a method that is not in the working copy: " + methodId, nameof(references));
            var next = Scalar(connection, transaction, "SELECT COALESCE(MAX(ordinal), -1) + 1 FROM source_references WHERE key_ordinal = $key", ("$key", key))!.Value;
            for (var i = 0; i < rows.Length; i++)
                InsertSourceReference(insert, key, next + i, rows[i], fileId);
            AlignReferenceOrder(connection, transaction, key, fileIds);
        }
    }

    private static void AlignReferenceOrder(SqliteConnection connection, SqliteTransaction transaction, long key, Dictionary<string, long> fileIds)
    {
        var stored = ReadKeyReferences(connection, transaction, key);
        var ordered = SolutionAnalysis.CanonicalReferences(stored);
        if (stored.SequenceEqual(ordered)) return;
        Execute("DELETE FROM source_references WHERE key_ordinal = $key", connection, transaction, ("$key", key));
        using var insert = Prepare(connection, transaction, InsertSourceReferenceSql);
        for (var i = 0; i < ordered.Length; i++)
            InsertSourceReference(insert, key, i, ordered[i], fileIds[ordered[i].Location.File]);
    }

    private static List<SourceReference> ReadKeyReferences(SqliteConnection connection, SqliteTransaction transaction, long key)
    {
        var result = new List<SourceReference>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT r.definition_id, r.project, r.kind, f.path, r.line, r.col
            FROM source_references r JOIN files f ON f.id = r.file_id
            WHERE r.key_ordinal = $key ORDER BY r.ordinal
            """;
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                new(reader.GetString(3), (int)reader.GetInt64(4), (int)reader.GetInt64(5))));
        return result;
    }

    private static long? Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, parameter) in parameters)
            command.Parameters.AddWithValue(name, parameter);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : (long)value;
    }

    private static void Execute(string sql, SqliteConnection connection, SqliteTransaction transaction, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}

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
    /// Records the SHA-256 of the text the file's rows were built from.
    /// Publication keeps this hash; freshness then reports stale when disk bytes differ.
    /// </summary>
    internal static void RecordAppliedText(string database, string absolutePath, string relativeFile, string text)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(text)));
        using var connection = OpenWorkingCopy(database);
        Execute(connection, "PRAGMA journal_mode=DELETE;");
        using var transaction = connection.BeginTransaction();
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
        transaction.Commit();
    }

    /// <summary>Gives a generation copy a new id. Tracked file hashes stay as recorded for the text that was indexed.</summary>
    internal static void PreparePublishedCopy(string path, string indexId)
    {
        using var connection = OpenWorkingCopy(path);
        Execute(connection, "PRAGMA journal_mode=DELETE;");
        using var transaction = connection.BeginTransaction();
        SetGenerationId(connection, transaction, indexId);
        transaction.Commit();
    }

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

using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodeAtlas.Storage;

/// <summary>
/// Read-only access to one immutable generation database. Each operation opens its own
/// connection (immutable, read-only, no pooling), so no file handle outlives a call.
/// SQLite corruption and format errors surface as <see cref="IndexFormatException"/>.
/// </summary>
internal sealed class SqliteIndexReader(string path)
{
    public string Path { get; } = path;

    /// <summary>Header, identity and completeness checks; reads only the small meta table.</summary>
    public CodebaseIndex ReadHeader(CancellationToken cancellationToken = default) => Use(session =>
    {
        session.RequireComplete();
        return session.Header(cancellationToken);
    });

    /// <summary>Materializes the complete index, equal to the deserialized JSON form of the same data.</summary>
    public CodebaseIndex ReadFull(bool includeCatalog, CancellationToken cancellationToken = default) => Use(session =>
    {
        session.RequireComplete();
        var header = session.Header(cancellationToken);
        var index = header with
        {
            Projects = session.Projects(),
            Types = session.Types(),
            Methods = session.Methods(null),
            References = session.AllReferences(cancellationToken),
            IncrementalState = session.IncrementalState()
        };
        if (!includeCatalog) return index;
        return index with
        {
            Symbols = session.HasSymbols ? session.Symbols(cancellationToken) : null,
            StructuralCatalog = session.StructuralCatalog(cancellationToken),
            Duplicates = session.Duplicates(cancellationToken)
        };
    });

    /// <summary>A long-lived read session for multi-statement operations; the caller disposes it.</summary>
    public Session OpenSession()
    {
        try
        {
            var session = new Session(Path);
            try { session.RequireComplete(); }
            catch { session.Dispose(); throw; }
            return session;
        }
        catch (SqliteException exception) { throw Translate(exception); }
    }

    public T Use<T>(Func<Session, T> action)
    {
        try
        {
            using var session = new Session(Path);
            return action(session);
        }
        catch (SqliteException exception)
        {
            throw Translate(exception);
        }
        catch (JsonException exception)
        {
            throw new IndexFormatException("invalid_index", "The index database contains malformed records. Rebuild the index.", [exception.Message]);
        }
    }

    internal static IndexFormatException Translate(SqliteException exception) => exception.SqliteErrorCode switch
    {
        // SQLITE_CORRUPT, SQLITE_NOTADB, SQLITE_FORMAT, SQLITE_RANGE, SQLITE_MISMATCH, SQLITE_ERROR (missing schema objects)
        11 or 26 or 24 or 25 or 20 or 1 => new("invalid_index", "The index database is damaged or incomplete. Rebuild the index.", [exception.Message]),
        // SQLITE_CANTOPEN, SQLITE_IOERR, SQLITE_PERM, SQLITE_BUSY/LOCKED
        14 or 10 or 3 or 5 or 6 or 23 => new("snapshot_io_failed", "Cannot read the index database. Check its path and permissions, or rebuild it.", [exception.Message]),
        _ => new("invalid_index", "The index database could not be read. Rebuild the index.", [exception.Message])
    };

    /// <summary>One read-only connection with prepared lookups and a file-path cache.</summary>
    internal sealed class Session : IDisposable
    {
        private readonly SqliteConnection connection;
        private Dictionary<long, string>? files;
        private Dictionary<string, string>? meta;
        private readonly Dictionary<string, SqliteCommand> commands = new(StringComparer.Ordinal);

        public Session(string path)
        {
            if (!File.Exists(path))
                throw new IndexFormatException("invalid_index", "The index database is missing. Rebuild the index.");
            // immutable=1: the committed generation never changes, so SQLite takes no locks and
            // creates no journal, WAL or shared-memory files next to it. Readers never write.
            var uri = new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri + "?immutable=1";
            var builder = new SqliteConnectionStringBuilder { DataSource = uri, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            connection = new SqliteConnection(builder.ToString());
            try
            {
                connection.Open();
                using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA cell_size_check=ON; PRAGMA query_only=ON;";
                pragma.ExecuteNonQuery();
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var command in commands.Values) command.Dispose();
            connection.Dispose();
        }

        public void RequireComplete()
        {
            if (Scalar<long>("PRAGMA application_id") != SqliteIndexSchema.ApplicationId)
                throw new IndexFormatException("invalid_index", "The file is not a CodeAtlas index database. Rebuild the index.");
            var version = Scalar<long>("PRAGMA user_version");
            if (version == 0)
                throw new IndexFormatException("invalid_index", "The index database is incomplete. Rebuild the index.");
            if (version != SqliteIndexSchema.StoreVersion)
                throw new IndexFormatException("unsupported_index", "Unsupported index database version. Rebuild the index with this version of CodeAtlas.");
        }

        public bool HasSymbols => Meta<bool>("hasSymbols");

        public T Meta<T>(string key)
        {
            meta ??= ReadMeta();
            if (!meta.TryGetValue(key, out var value))
                throw new IndexFormatException("invalid_index", "The index database header is incomplete. Rebuild the index.");
            return JsonSerializer.Deserialize<T>(value, SqliteIndexSchema.Json)!;
        }

        private Dictionary<string, string> ReadMeta()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            using var reader = Reader("meta", "SELECT key, value FROM meta");
            while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
            return result;
        }

        public CodebaseIndex Header(CancellationToken cancellationToken)
        {
            SnapshotInputManifest? manifest = null;
            if (Meta<bool>("hasInputManifest"))
            {
                var stored = Meta<SnapshotInputManifest>("inputManifest");
                var inputFiles = new Dictionary<string, FileFingerprint>();
                using var reader = Reader("input_files", "SELECT path, present, sha256 FROM input_files ORDER BY ordinal");
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    inputFiles[reader.GetString(0)] = new(reader.GetInt64(1) == 1, reader.IsDBNull(2) ? null : reader.GetString(2));
                }
                manifest = stored with { Files = inputFiles };
            }
            var fingerprints = new Dictionary<string, string>();
            using (var reader = Reader("fingerprints", "SELECT path, sha256 FROM fingerprints ORDER BY ordinal"))
                while (reader.Read()) fingerprints[reader.GetString(0)] = reader.GetString(1);
            return new CodebaseIndex
            {
                SchemaVersion = Meta<int>("schemaVersion"),
                ToolVersion = Meta<string?>("toolVersion"),
                IndexId = Meta<string>("indexId"),
                Solution = Meta<string>("solution"),
                CreatedAtUtc = Meta<DateTimeOffset>("createdAtUtc"),
                Warnings = Meta<string[]>("warnings"),
                InputRoots = Meta<string[]>("inputRoots"),
                ExplicitInputs = Meta<string[]>("explicitInputs"),
                Refresh = Meta<RefreshSummary?>("refresh"),
                VirtualSnapshot = Meta<string?>("virtualSnapshot"),
                DocumentationCoverage = Meta<DocumentationCoverage?>("documentationCoverage"),
                InputManifest = manifest,
                Fingerprints = fingerprints,
                Projects = [],
                Types = [],
                Methods = [],
                References = []
            };
        }

        public StructuralCoverage? StructuralCoverage => Meta<StructuralCoverage?>("structuralCoverage");

        public IndexedProject[] Projects()
        {
            var result = new List<IndexedProject>();
            using var reader = Reader("projects", "SELECT path, name, dependencies, files FROM projects ORDER BY ordinal");
            while (reader.Read())
                result.Add(new(reader.GetString(0), reader.GetString(1), Json<string[]>(reader.GetString(2)), Json<string[]>(reader.GetString(3))));
            return result.ToArray();
        }

        public IndexedType[] Types()
        {
            var result = new List<IndexedType>();
            using var reader = Reader("types", "SELECT id, name, kind, project, file_id, line, col FROM types ORDER BY ordinal");
            while (reader.Read())
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Position(reader, 4)));
            return result.ToArray();
        }

        private const string MethodColumns = "SELECT id, name, signature, project, containing_type, file_id, line, col FROM methods";

        /// <summary>All methods, or those with an exact (ordinal) name, in stored order.</summary>
        public MethodInfo[] Methods(string? name)
        {
            using var reader = name is null ? Reader("methods", MethodColumns + " ORDER BY ordinal")
                : Reader("methods-name", MethodColumns + " WHERE name = $p0 ORDER BY ordinal", name);
            return ReadMethods(reader);
        }

        public bool HasMethod(string id) => Exists("method-id", "SELECT 1 FROM methods WHERE id = $p0 LIMIT 1", id);

        public bool HasSymbol(string id) => Exists("symbol-id", "SELECT 1 FROM symbols WHERE id = $p0 LIMIT 1", id);

        private MethodInfo[] ReadMethods(SqliteDataReader reader)
        {
            var result = new List<MethodInfo>();
            while (reader.Read())
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Position(reader, 5))
                    { ContainingType = reader.IsDBNull(4) ? null : reader.GetString(4) });
            return result.ToArray();
        }

        /// <summary>Stored references for one method key; null when the ID is not a reference key.</summary>
        public SourceReference[]? References(string methodId)
        {
            long key;
            using (var reader = Reader("reference-key", "SELECT ordinal FROM reference_keys WHERE method_id = $p0 ORDER BY ordinal LIMIT 1", methodId))
            {
                if (!reader.Read()) return null;
                key = reader.GetInt64(0);
            }
            var result = new List<SourceReference>();
            using (var reader = Reader("references-of", "SELECT definition_id, project, kind, file_id, line, col FROM source_references WHERE key_ordinal = $p0 ORDER BY ordinal", key))
                while (reader.Read())
                    result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), Position(reader, 3)));
            return result.ToArray();
        }

        public Dictionary<string, SourceReference[]> AllReferences(CancellationToken cancellationToken)
        {
            var keys = new List<string>();
            using (var reader = Reader("reference-keys", "SELECT method_id FROM reference_keys ORDER BY ordinal"))
                while (reader.Read()) keys.Add(reader.GetString(0));
            var groups = new List<SourceReference>[keys.Count];
            for (var i = 0; i < groups.Length; i++) groups[i] = [];
            using (var reader = Reader("references", "SELECT key_ordinal, definition_id, project, kind, file_id, line, col FROM source_references ORDER BY key_ordinal, ordinal"))
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = reader.GetInt64(0);
                    if (key < 0 || key >= groups.Length)
                        throw new IndexFormatException("invalid_index", "The index database has orphaned references. Rebuild the index.");
                    groups[key].Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3), Position(reader, 4)));
                }
            var result = new Dictionary<string, SourceReference[]>();
            for (var i = 0; i < keys.Count; i++) result[keys[i]] = groups[i].ToArray();
            return result;
        }

        private const string SymbolColumns = "SELECT s.ordinal, s.id, s.name, s.kind, s.type_kind, s.signature, s.project, s.namespace, "
            + "s.containing_type, s.file_id, s.line, s.col, s.locations, d.value FROM symbols s JOIN documentation d ON d.symbol_ordinal = s.ordinal";

        public DocumentedSymbol[] Symbols(CancellationToken cancellationToken)
        {
            using var reader = Reader("symbols", SymbolColumns + " ORDER BY s.ordinal");
            return ReadSymbols(reader, cancellationToken);
        }

        public DocumentedSymbol? Symbol(string id)
        {
            using var reader = Reader("symbol", SymbolColumns + " WHERE s.id = $p0 ORDER BY s.ordinal", id);
            var found = ReadSymbols(reader, CancellationToken.None);
            // Matches DocumentationQueries.GetDocumentation's SingleOrDefault over unique, validated IDs.
            return found.Length switch
            {
                0 => null,
                1 => found[0],
                _ => throw new IndexFormatException("invalid_index", "Duplicate symbol IDs in the index database. Rebuild the index.")
            };
        }

        public DocumentedSymbol? SymbolAt(long ordinal)
        {
            using var reader = Reader("symbol-ordinal", SymbolColumns + " WHERE s.ordinal = $p0", ordinal);
            var found = ReadSymbols(reader, CancellationToken.None);
            return found.Length == 0 ? null : found[0];
        }

        /// <summary>Exact name; kind "all" or one symbol kind. Stored order.</summary>
        public DocumentedSymbol[] SymbolsNamed(string name, string kind)
        {
            using var reader = kind == "all"
                ? Reader("symbols-name", SymbolColumns + " WHERE s.name = $p0 ORDER BY s.ordinal", name)
                : Reader("symbols-name-kind", SymbolColumns + " WHERE s.name = $p0 AND s.kind = $p1 ORDER BY s.ordinal", name, kind);
            return ReadSymbols(reader, CancellationToken.None);
        }

        public string? SymbolKind(string id) =>
            ScalarOrDefault<string>("symbol-kind", "SELECT kind FROM symbols WHERE id = $p0 ORDER BY ordinal LIMIT 1", id);

        public long SymbolCount(string kind) => kind == "all"
            ? Scalar<long>("SELECT count(*) FROM symbols")
            : ScalarOrDefault<long>("symbol-count", "SELECT count(*) FROM symbols WHERE kind = $p0", kind);

        private DocumentedSymbol[] ReadSymbols(SqliteDataReader reader, CancellationToken cancellationToken)
        {
            var result = new List<DocumentedSymbol>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(ReadSymbol(reader));
            }
            return result.ToArray();
        }

        private DocumentedSymbol ReadSymbol(SqliteDataReader reader) =>
            new(reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), Position(reader, 9),
                Json<SourcePosition[]>(reader.GetString(12)), Json<DeveloperDocumentation>(reader.GetString(13)));

        /// <summary>Symbols for these ordinals. A missing ordinal is absent from the dictionary.</summary>
        public Dictionary<long, DocumentedSymbol> SymbolsForOrdinals(IReadOnlyList<long> ordinals)
        {
            var result = new Dictionary<long, DocumentedSymbol>(ordinals.Count);
            ReadChunks(SymbolColumns + " WHERE s.ordinal IN (", ")", ordinals, reader =>
            {
                while (reader.Read()) result[reader.GetInt64(0)] = ReadSymbol(reader);
            });
            return result;
        }

        /// <summary>Adds each id, or null when the symbol row is missing. Duplicate ids are an invalid index.</summary>
        public void RememberSymbols(IReadOnlyList<string> ids, Dictionary<string, DocumentedSymbol?> into)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            ReadChunks(SymbolColumns + " WHERE s.id IN (", ")", ids, reader =>
            {
                while (reader.Read())
                {
                    var symbol = ReadSymbol(reader);
                    if (!found.Add(symbol.Id))
                        throw new IndexFormatException("invalid_index", "Duplicate symbol IDs in the index database. Rebuild the index.");
                    into[symbol.Id] = symbol;
                }
            });
            foreach (var id in ids)
                if (!into.ContainsKey(id)) into[id] = null;
        }

        private const string FactColumns = "SELECT owner_id, kind, target_name, target_signature, target_declaration_id, "
            + "target_source_symbol_id, target_assembly, target_resolution, origin, file_id, start_line, start_col, "
            + "end_file_id, end_line, end_col, semantic_scope FROM facts";

        public long FactCount() => Scalar<long>("SELECT count(*) FROM facts");

        public StructuralFact[] FactsOwnedBy(string ownerId)
        {
            using var reader = Reader("facts-owner", FactColumns + " WHERE owner_id = $p0 ORDER BY ordinal", ownerId);
            return ReadFacts(reader, CancellationToken.None);
        }

        /// <summary>Facts for each owner, in stored ordinal order. Owners with no facts map to an empty array.</summary>
        public Dictionary<string, StructuralFact[]> FactsForOwners(IReadOnlyList<string> ownerIds)
        {
            var lists = new Dictionary<string, List<StructuralFact>>(ownerIds.Count, StringComparer.Ordinal);
            foreach (var id in ownerIds)
                if (!lists.ContainsKey(id)) lists[id] = [];
            ReadChunks(FactColumns + " WHERE owner_id IN (", ") ORDER BY ordinal", ownerIds, reader =>
            {
                while (reader.Read())
                {
                    var fact = ReadFact(reader);
                    if (lists.TryGetValue(fact.OwnerId, out var owned)) owned.Add(fact);
                }
            });
            return lists.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
        }

        /// <summary>Resolved invocations of one source method among the first <paramref name="scanLimit"/> stored facts.</summary>
        public StructuralFact[] IncomingInvocations(string targetId, long scanLimit)
        {
            using var reader = Reader("facts-incoming", FactColumns
                + " WHERE target_source_symbol_id = $p0 AND kind = 'resolved-invocation' AND ordinal < $p1 ORDER BY ordinal", targetId, scanLimit);
            return ReadFacts(reader, CancellationToken.None);
        }

        public StructuralCatalog? StructuralCatalog(CancellationToken cancellationToken)
        {
            if (StructuralCoverage is not { } coverage) return null;
            StructuralFact[] facts;
            using (var reader = Reader("facts", FactColumns + " ORDER BY ordinal"))
                facts = ReadFacts(reader, cancellationToken);
            var issues = new List<StructuralIssue>();
            using (var reader = Reader("issues", "SELECT value FROM structural_issues ORDER BY ordinal"))
                while (reader.Read()) issues.Add(Json<StructuralIssue>(reader.GetString(0)));
            return new(coverage, facts, issues.ToArray());
        }

        private StructuralFact[] ReadFacts(SqliteDataReader reader, CancellationToken cancellationToken)
        {
            var result = new List<StructuralFact>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(ReadFact(reader));
            }
            return result.ToArray();
        }

        private StructuralFact ReadFact(SqliteDataReader reader)
        {
            var target = new FactTarget(reader.GetString(2), reader.GetString(3), Text(reader, 4), Text(reader, 5),
                Text(reader, 6), reader.GetString(7));
            var range = new DocumentationRange(Position(reader, 9), Position(reader, 12));
            return new(reader.GetString(0), reader.GetString(1), target,
                new StructuralEvidence(reader.GetString(8), range, reader.GetString(15)));
        }

        private void ReadChunks<T>(string before, string after, IReadOnlyList<T> keys, Action<SqliteDataReader> read)
        {
            const int chunkSize = 400;
            for (var start = 0; start < keys.Count; start += chunkSize)
            {
                var count = Math.Min(chunkSize, keys.Count - start);
                using var command = connection.CreateCommand();
                var placeholders = new string[count];
                for (var i = 0; i < count; i++)
                {
                    placeholders[i] = "$k" + i;
                    command.Parameters.AddWithValue(placeholders[i], keys[start + i]!);
                }
                command.CommandText = before + string.Join(",", placeholders) + after;
                using var reader = command.ExecuteReader();
                read(reader);
            }
        }

        public DuplicateCatalog? Duplicates(CancellationToken cancellationToken)
        {
            if (!Meta<bool>("hasDuplicates")) return null;
            var header = DuplicateHeader();
            var groups = JsonRows<DuplicateGroup>("duplicate-groups", "SELECT value FROM duplicate_groups ORDER BY ordinal", cancellationToken);
            return header with
            {
                Groups = groups,
                Analysis = header.Analysis is { } analysis ? analysis with
                {
                    Vocabulary = Strings("duplicate-vocabulary", "SELECT token FROM duplicate_vocabulary ORDER BY ordinal", cancellationToken),
                    Bodies = JsonRows<DuplicateBody>("duplicate-bodies", "SELECT value FROM duplicate_bodies ORDER BY ordinal", cancellationToken),
                    Omissions = JsonRows<DuplicateOmission>("duplicate-body-omissions", "SELECT value FROM duplicate_body_omissions ORDER BY ordinal", cancellationToken)
                } : null,
                Scan = header.Scan is { } scan ? scan with
                {
                    Candidates = JsonRows<DuplicateCandidate>("duplicate-candidates", "SELECT value FROM duplicate_candidates ORDER BY ordinal", cancellationToken),
                    Omissions = JsonRows<DuplicateOmission>("duplicate-scan-omissions", "SELECT value FROM duplicate_scan_omissions ORDER BY ordinal", cancellationToken)
                } : null
            };
        }

        /// <summary>Duplicate catalog without groups, vocabulary, bodies, candidates or omissions.</summary>
        public DuplicateCatalog DuplicateHeader() => Meta<DuplicateCatalog>("duplicates");

        public bool HasDuplicates => Meta<bool>("hasDuplicates");

        public DuplicateCandidate[] DuplicateCandidates(string? symbolId, string? project)
        {
            using var reader = Reader("duplicate-candidates-filter",
                "SELECT value FROM duplicate_candidates WHERE ($p0 IS NULL OR left_symbol = $p0 OR right_symbol = $p0) "
                + "AND ($p1 IS NULL OR left_project = $p1 OR right_project = $p1) ORDER BY ordinal", symbolId, project);
            var result = new List<DuplicateCandidate>();
            while (reader.Read()) result.Add(Json<DuplicateCandidate>(reader.GetString(0)));
            return result.ToArray();
        }

        /// <summary>Token count of the stored body for one symbol; null when no body was extracted.</summary>
        public int? DuplicateBodyTokenCount(string symbolId)
        {
            using var reader = Reader("duplicate-body-tokens", "SELECT token_count FROM duplicate_bodies WHERE symbol_id = $p0 ORDER BY ordinal", symbolId);
            if (!reader.Read()) return null;
            var count = reader.GetInt32(0);
            if (reader.Read())
                throw new IndexFormatException("invalid_index", "Duplicate body owners are not unique. Rebuild the index.");
            return count;
        }

        public string[] DuplicateOmissionReasons(string symbolId)
        {
            var result = new List<string>();
            using var reader = Reader("duplicate-omission-reasons", "SELECT value FROM duplicate_body_omissions WHERE symbol_id = $p0 ORDER BY ordinal", symbolId);
            while (reader.Read()) result.Add(Json<DuplicateOmission>(reader.GetString(0)).Reason);
            return result.ToArray();
        }

        public int DuplicateOmittedMethodCount() =>
            checked((int)Scalar<long>("SELECT count(DISTINCT symbol_id) FROM duplicate_body_omissions"));

        public IncrementalSnapshotState? IncrementalState()
        {
            if (!Meta<bool>("hasIncrementalState")) return null;
            var state = Meta<IncrementalSnapshotState>("incrementalState");
            var projects = new List<(string Project, string Hash)>();
            using (var reader = Reader("incremental-projects", "SELECT project, context_hash FROM incremental_projects ORDER BY ordinal"))
                while (reader.Read()) projects.Add((reader.GetString(0), reader.GetString(1)));
            var trees = projects.Select(_ => new List<IncrementalTreeState>()).ToArray();
            using (var reader = Reader("incremental-trees", "SELECT project_ordinal, key, project, origin_path, physical_path, searchable, content_hash, surface_hash, parse_options_hash FROM incremental_trees ORDER BY project_ordinal, ordinal"))
                while (reader.Read())
                {
                    var project = reader.GetInt64(0);
                    if (project < 0 || project >= trees.Length)
                        throw new IndexFormatException("invalid_index", "The index database has orphaned incremental state. Rebuild the index.");
                    trees[project].Add(new(reader.GetString(1), reader.GetString(2), reader.GetString(3), Text(reader, 4),
                        reader.GetInt64(5) == 1, reader.GetString(6), Text(reader, 7), reader.GetString(8)));
                }
            return state with { Projects = projects.Select((project, i) => new IncrementalProjectState(project.Project, project.Hash, trees[i].ToArray())).ToArray() };
        }

        // Search corpus support.
        public Dictionary<string, (long Length, int Count)> SearchFieldLengths(string kind)
        {
            var result = new Dictionary<string, (long Length, int Count)>(StringComparer.Ordinal);
            using var reader = Reader("search-lengths", "SELECT family, total, count FROM search_field_lengths WHERE kind = $p0", kind);
            while (reader.Read()) result[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt32(2));
            return result;
        }

        /// <summary>Search postings of symbols located in one file, in symbol, token, then name-field order.</summary>
        public (string SymbolId, string Token, bool NameField)[] SearchPostingsForFile(string relativeFile)
        {
            var result = new List<(string, string, bool)>();
            using var reader = Reader("search-postings-file", """
                SELECT s.id, t.token, p.name_field
                FROM search_postings p
                JOIN search_tokens t ON t.id = p.token_id
                JOIN symbols s ON s.ordinal = p.symbol_ordinal
                JOIN files f ON f.id = s.file_id
                WHERE f.path = $p0
                ORDER BY s.id, t.token, p.name_field
                """, relativeFile);
            while (reader.Read()) result.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0));
            return result.ToArray();
        }

        /// <summary>Symbol ordinals whose stored fields contain the token, restricted to kind and name fields.</summary>
        public HashSet<long> Postings(string token, string kind, bool nameFieldsOnly)
        {
            var result = new HashSet<long>();
            var sql = "SELECT p.symbol_ordinal FROM search_tokens t JOIN search_postings p ON p.token_id = t.id"
                + (kind == "all" ? "" : " JOIN symbols s ON s.ordinal = p.symbol_ordinal")
                + " WHERE t.token = $p0" + (nameFieldsOnly ? " AND p.name_field = 1" : "") + (kind == "all" ? "" : " AND s.kind = $p1");
            using var reader = kind == "all"
                ? Reader("postings" + nameFieldsOnly, sql, token)
                : Reader("postings-kind" + nameFieldsOnly, sql, token, kind);
            while (reader.Read()) result.Add(reader.GetInt64(0));
            return result;
        }

        public long? SymbolOrdinal(string id) =>
            ScalarOrDefault<long?>("symbol-ordinal-id", "SELECT ordinal FROM symbols WHERE id = $p0 ORDER BY ordinal LIMIT 1", id);

        public string[] SymbolIds()
        {
            var result = new List<string>();
            using var reader = Reader("symbol-ids", "SELECT id FROM symbols ORDER BY ordinal");
            while (reader.Read()) result.Add(reader.GetString(0));
            return result.ToArray();
        }

        public bool HasSearchIndex => Scalar<long>("SELECT count(*) FROM search_field_lengths") > 0
            || Scalar<long>("SELECT count(*) FROM symbols") == 0;

        private string[] Strings(string name, string sql, CancellationToken cancellationToken)
        {
            var result = new List<string>();
            using var reader = Reader(name, sql);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(reader.GetString(0));
            }
            return result.ToArray();
        }

        private T[] JsonRows<T>(string name, string sql, CancellationToken cancellationToken)
        {
            var result = new List<T>();
            using var reader = Reader(name, sql);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(Json<T>(reader.GetString(0)));
            }
            return result.ToArray();
        }

        private SourcePosition Position(SqliteDataReader reader, int column) =>
            new(FilePath(reader.GetInt64(column)), reader.GetInt32(column + 1), reader.GetInt32(column + 2));

        private string FilePath(long id)
        {
            if (files is null)
            {
                files = new Dictionary<long, string>();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, path FROM files";
                using var reader = command.ExecuteReader();
                while (reader.Read()) files[reader.GetInt64(0)] = reader.GetString(1);
            }
            return files.TryGetValue(id, out var path) ? path
                : throw new IndexFormatException("invalid_index", "The index database references an unknown file. Rebuild the index.");
        }

        private static string? Text(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);

        private static T Json<T>(string value) => JsonSerializer.Deserialize<T>(value, SqliteIndexSchema.Json)
            ?? throw new IndexFormatException("invalid_index", "The index database contains an empty record. Rebuild the index.");

        private T Scalar<T>(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
        }

        private T? ScalarOrDefault<T>(string name, string sql, params object?[] values)
        {
            using var reader = Reader(name, sql, values);
            if (!reader.Read() || reader.IsDBNull(0)) return default;
            var value = reader.GetValue(0);
            var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            return (T)Convert.ChangeType(value, type);
        }

        private bool Exists(string name, string sql, params object?[] values)
        {
            using var reader = Reader(name, sql, values);
            return reader.Read();
        }

        private SqliteDataReader Reader(string name, string sql, params object?[] values)
        {
            if (!commands.TryGetValue(name, out var command))
            {
                command = connection.CreateCommand();
                command.CommandText = sql;
                for (var i = 0; i < values.Length; i++)
                    command.Parameters.Add(new SqliteParameter { ParameterName = "$p" + i });
                commands.Add(name, command);
            }
            for (var i = 0; i < values.Length; i++)
                command.Parameters[i].Value = values[i] ?? DBNull.Value;
            return command.ExecuteReader();
        }
    }
}

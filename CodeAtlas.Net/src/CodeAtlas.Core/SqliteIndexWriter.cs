using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodeAtlas.Storage;

/// <summary>
/// Layout of one immutable generation database. Rows that describe source carry a file id so a
/// later per-file update can replace one file's rows in a transaction; lookups use symbol id, name,
/// containing type, file and project indexes. Small nested records are stored as JSON text.
/// </summary>
internal static class SqliteIndexSchema
{
    /// <summary>Written last (PRAGMA user_version); a database without it is incomplete.</summary>
    public const int StoreVersion = 1;
    /// <summary>"CAtl": identifies a CodeAtlas index database (PRAGMA application_id).</summary>
    public const int ApplicationId = 0x43417463;
    public const string ArtifactName = "index.db";

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public const string Tables = """
        CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
        CREATE TABLE files(id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, content_hash TEXT);
        CREATE TABLE projects(ordinal INTEGER PRIMARY KEY, path TEXT NOT NULL, name TEXT NOT NULL,
            dependencies TEXT NOT NULL, files TEXT NOT NULL);
        CREATE TABLE types(ordinal INTEGER PRIMARY KEY, id TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL,
            project TEXT NOT NULL, file_id INTEGER NOT NULL, line INTEGER NOT NULL, col INTEGER NOT NULL);
        CREATE TABLE methods(ordinal INTEGER PRIMARY KEY, id TEXT NOT NULL, name TEXT NOT NULL, signature TEXT NOT NULL,
            project TEXT NOT NULL, containing_type TEXT, file_id INTEGER NOT NULL, line INTEGER NOT NULL, col INTEGER NOT NULL);
        CREATE TABLE reference_keys(ordinal INTEGER PRIMARY KEY, method_id TEXT NOT NULL);
        CREATE TABLE source_references(key_ordinal INTEGER NOT NULL, ordinal INTEGER NOT NULL, definition_id TEXT NOT NULL,
            project TEXT NOT NULL, kind TEXT NOT NULL, file_id INTEGER NOT NULL, line INTEGER NOT NULL, col INTEGER NOT NULL,
            PRIMARY KEY(key_ordinal, ordinal)) WITHOUT ROWID;
        CREATE TABLE symbols(ordinal INTEGER PRIMARY KEY, id TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL,
            type_kind TEXT, signature TEXT NOT NULL, project TEXT NOT NULL, namespace TEXT NOT NULL, containing_type TEXT,
            file_id INTEGER NOT NULL, line INTEGER NOT NULL, col INTEGER NOT NULL, locations TEXT NOT NULL);
        CREATE TABLE documentation(symbol_ordinal INTEGER PRIMARY KEY, status TEXT NOT NULL, value TEXT NOT NULL);
        CREATE TABLE facts(ordinal INTEGER PRIMARY KEY, owner_id TEXT NOT NULL, kind TEXT NOT NULL,
            target_name TEXT NOT NULL, target_signature TEXT NOT NULL, target_declaration_id TEXT,
            target_source_symbol_id TEXT, target_assembly TEXT, target_resolution TEXT NOT NULL,
            origin TEXT NOT NULL, file_id INTEGER NOT NULL, start_line INTEGER NOT NULL, start_col INTEGER NOT NULL,
            end_file_id INTEGER NOT NULL, end_line INTEGER NOT NULL, end_col INTEGER NOT NULL, semantic_scope TEXT NOT NULL);
        CREATE TABLE structural_issues(ordinal INTEGER PRIMARY KEY, owner_id TEXT NOT NULL, code TEXT NOT NULL,
            file_id INTEGER NOT NULL, value TEXT NOT NULL);
        CREATE TABLE duplicate_groups(ordinal INTEGER PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE duplicate_vocabulary(ordinal INTEGER PRIMARY KEY, token TEXT NOT NULL);
        CREATE TABLE duplicate_bodies(ordinal INTEGER PRIMARY KEY, symbol_id TEXT NOT NULL, file_id INTEGER NOT NULL,
            token_count INTEGER NOT NULL, value TEXT NOT NULL);
        CREATE TABLE duplicate_body_omissions(ordinal INTEGER PRIMARY KEY, symbol_id TEXT NOT NULL, value TEXT NOT NULL);
        CREATE TABLE duplicate_candidates(ordinal INTEGER PRIMARY KEY, left_symbol TEXT NOT NULL, right_symbol TEXT NOT NULL,
            left_project TEXT NOT NULL, right_project TEXT NOT NULL, value TEXT NOT NULL);
        CREATE TABLE duplicate_scan_omissions(ordinal INTEGER PRIMARY KEY, symbol_id TEXT NOT NULL, value TEXT NOT NULL);
        CREATE TABLE input_files(ordinal INTEGER PRIMARY KEY, path TEXT NOT NULL, present INTEGER NOT NULL, sha256 TEXT);
        CREATE TABLE fingerprints(ordinal INTEGER PRIMARY KEY, path TEXT NOT NULL, sha256 TEXT NOT NULL);
        CREATE TABLE incremental_projects(ordinal INTEGER PRIMARY KEY, project TEXT NOT NULL, context_hash TEXT NOT NULL);
        CREATE TABLE incremental_trees(project_ordinal INTEGER NOT NULL, ordinal INTEGER NOT NULL, key TEXT NOT NULL,
            project TEXT NOT NULL, origin_path TEXT NOT NULL, physical_path TEXT, searchable INTEGER NOT NULL,
            content_hash TEXT NOT NULL, surface_hash TEXT, parse_options_hash TEXT NOT NULL,
            PRIMARY KEY(project_ordinal, ordinal)) WITHOUT ROWID;
        CREATE TABLE search_tokens(id INTEGER PRIMARY KEY, token TEXT NOT NULL UNIQUE);
        CREATE TABLE search_postings(token_id INTEGER NOT NULL, symbol_ordinal INTEGER NOT NULL, name_field INTEGER NOT NULL,
            PRIMARY KEY(token_id, symbol_ordinal)) WITHOUT ROWID;
        CREATE TABLE search_field_lengths(kind TEXT NOT NULL, family TEXT NOT NULL, total INTEGER NOT NULL,
            count INTEGER NOT NULL, PRIMARY KEY(kind, family)) WITHOUT ROWID;
        """;

    // Created after bulk loading. Every source-row table is indexed by file for per-file replacement.
    public const string Indexes = """
        CREATE INDEX types_id ON types(id);
        CREATE INDEX types_file ON types(file_id);
        CREATE INDEX methods_id ON methods(id);
        CREATE INDEX methods_name ON methods(name);
        CREATE INDEX methods_type ON methods(containing_type);
        CREATE INDEX methods_project ON methods(project);
        CREATE INDEX methods_file ON methods(file_id);
        CREATE INDEX reference_keys_method ON reference_keys(method_id);
        CREATE INDEX source_references_file ON source_references(file_id);
        CREATE INDEX symbols_id ON symbols(id);
        CREATE INDEX symbols_name ON symbols(name, kind);
        CREATE INDEX symbols_kind ON symbols(kind);
        CREATE INDEX symbols_type ON symbols(containing_type);
        CREATE INDEX symbols_project ON symbols(project);
        CREATE INDEX symbols_file ON symbols(file_id);
        CREATE INDEX facts_owner ON facts(owner_id);
        CREATE INDEX facts_target ON facts(target_source_symbol_id, kind);
        CREATE INDEX facts_file ON facts(file_id);
        CREATE INDEX structural_issues_owner ON structural_issues(owner_id);
        CREATE INDEX duplicate_bodies_symbol ON duplicate_bodies(symbol_id);
        CREATE INDEX duplicate_bodies_file ON duplicate_bodies(file_id);
        CREATE INDEX duplicate_body_omissions_symbol ON duplicate_body_omissions(symbol_id);
        CREATE INDEX duplicate_candidates_left ON duplicate_candidates(left_symbol);
        CREATE INDEX duplicate_candidates_right ON duplicate_candidates(right_symbol);
        CREATE INDEX search_postings_symbol ON search_postings(symbol_ordinal);
        """;
}

/// <summary>
/// Writes one complete generation database in bounded transactions. The file is private to the
/// writer until the generation manifest commits; it is never modified after <see cref="Complete"/>.
/// </summary>
internal sealed class SqliteIndexWriter : IDisposable
{
    private const int BatchRows = 20_000;
    private readonly SqliteConnection connection;
    private readonly Dictionary<string, long> fileIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> contentHashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SqliteCommand> commands = new(StringComparer.Ordinal);
    private SqliteTransaction? transaction;
    private int pending;
    private bool completed;

    private SqliteIndexWriter(SqliteConnection connection) => this.connection = connection;

    public static SqliteIndexWriter Create(string path)
    {
        SqliteAvailability.Require();
        if (File.Exists(path))
            throw new IOException("The index database already exists: " + path);
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            // No journal: an interrupted writer leaves an unreferenced, unversioned file that no
            // reader accepts. Durability is established by flushing the closed file before commit.
            Execute(connection, $"""
                PRAGMA journal_mode=OFF;
                PRAGMA synchronous=OFF;
                PRAGMA temp_store=MEMORY;
                PRAGMA cache_size=-16384;
                PRAGMA application_id={SqliteIndexSchema.ApplicationId};
                PRAGMA user_version=0;
                """);
            Execute(connection, SqliteIndexSchema.Tables);
            var writer = new SqliteIndexWriter(connection);
            writer.Begin();
            return writer;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Writes a complete in-memory index, section by section.</summary>
    public static void Write(string path, CodebaseIndex index, CancellationToken cancellationToken = default)
    {
        using var writer = Create(path);
        writer.WriteHeader(index);
        writer.WriteProjects(index.Projects);
        writer.WriteTypes(index.Types);
        writer.WriteMethods(index.Methods);
        writer.WriteReferences(index.References, cancellationToken);
        if (index.Symbols is { } symbols)
            writer.WriteSymbols(symbols, cancellationToken);
        if (index.StructuralCatalog is { } catalog)
        {
            writer.WriteStructuralCatalog(catalog, cancellationToken);
            if (index.Symbols is { } searchable)
                writer.WriteSearchIndex(searchable, catalog, cancellationToken);
        }
        if (index.Duplicates is { } duplicates)
            writer.WriteDuplicates(duplicates, cancellationToken);
        writer.WriteInputs(index, cancellationToken);
        if (index.IncrementalState is { } state)
            writer.WriteIncrementalState(state);
        writer.Complete(cancellationToken);
    }

    /// <summary>
    /// Records source-file hashes before any row that stores a file id. Row writes copy the hash
    /// into <c>files</c>; calling this first lets a caller write rows before the header meta.
    /// </summary>
    public void SeedContentHashes(string solution, SnapshotInputManifest? manifest)
    {
        if (manifest is null) return;
        var solutionDirectory = Path.GetDirectoryName(solution)!;
        foreach (var (path, fingerprint) in manifest.Files)
            if (fingerprint.Exists && fingerprint.Sha256 is { } hash && Path.IsPathFullyQualified(path))
                contentHashes.TryAdd(Path.GetRelativePath(solutionDirectory, path).Replace('\\', '/'), hash);
    }

    public void WriteHeader(CodebaseIndex index)
    {
        SeedContentHashes(index.Solution, index.InputManifest);
        Meta("schemaVersion", index.SchemaVersion);
        Meta("toolVersion", index.ToolVersion);
        Meta("indexId", index.IndexId);
        Meta("solution", index.Solution);
        Meta("createdAtUtc", index.CreatedAtUtc);
        Meta("warnings", index.Warnings);
        Meta("inputRoots", index.InputRoots);
        Meta("explicitInputs", index.ExplicitInputs);
        Meta("refresh", index.Refresh);
        Meta("virtualSnapshot", index.VirtualSnapshot);
        Meta("documentationCoverage", index.DocumentationCoverage);
        Meta("hasSymbols", index.Symbols is not null);
        Meta("structuralCoverage", index.StructuralCatalog?.Coverage);
        Meta("hasInputManifest", index.InputManifest is not null);
        // File fingerprints are rows; the remaining manifest is small and kept verbatim.
        Meta("inputManifest", index.InputManifest is { } inputs ? inputs with { Files = [] } : null);
        Meta("hasIncrementalState", index.IncrementalState is not null);
        Meta("incrementalState", index.IncrementalState is { } state ? state with { Projects = [] } : null);
        Meta("hasDuplicates", index.Duplicates is not null);
        Meta("duplicates", index.Duplicates is { } duplicates ? duplicates with
        {
            Groups = [],
            Analysis = duplicates.Analysis is { } analysis ? analysis with { Vocabulary = [], Bodies = [], Omissions = [] } : null,
            Scan = duplicates.Scan is { } scan ? scan with { Candidates = [], Omissions = [] } : null
        } : null);
        Meta("counts", new
        {
            projects = index.Projects.Length, types = index.Types.Length, methods = index.Methods.Length,
            symbols = index.Symbols?.Length, facts = index.StructuralCatalog?.Facts.Length
        });
    }

    public void WriteProjects(IndexedProject[] projects)
    {
        var command = Command("projects", "INSERT INTO projects VALUES($o,$path,$name,$deps,$files)");
        for (var i = 0; i < projects.Length; i++)
        {
            var project = projects[i];
            Run(command, i, project.Path, project.Name, Serialize(project.Dependencies), Serialize(project.Files));
        }
    }

    public void WriteTypes(IndexedType[] types)
    {
        var command = Command("types", "INSERT INTO types VALUES($o,$id,$name,$kind,$project,$file,$line,$col)");
        for (var i = 0; i < types.Length; i++)
        {
            var type = types[i];
            Run(command, i, type.Id, type.Name, type.Kind, type.Project, FileId(type.Location.File), type.Location.Line, type.Location.Column);
        }
    }

    public void WriteMethods(MethodInfo[] methods)
    {
        var command = Command("methods", "INSERT INTO methods VALUES($o,$id,$name,$sig,$project,$type,$file,$line,$col)");
        for (var i = 0; i < methods.Length; i++)
        {
            var method = methods[i];
            Run(command, i, method.Id, method.Name, method.Signature, method.Project, method.ContainingType,
                FileId(method.Location.File), method.Location.Line, method.Location.Column);
        }
    }

    public void WriteReferences(IEnumerable<KeyValuePair<string, SourceReference[]>> references, CancellationToken cancellationToken)
    {
        var keys = Command("reference_keys", "INSERT INTO reference_keys VALUES($o,$id)");
        var rows = Command("source_references", "INSERT INTO source_references VALUES($k,$o,$def,$project,$kind,$file,$line,$col)");
        var key = 0;
        foreach (var (methodId, items) in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Run(keys, key, methodId);
            for (var i = 0; i < items.Length; i++)
            {
                var reference = items[i];
                Run(rows, key, i, reference.DefinitionId, reference.Project, reference.Kind,
                    FileId(reference.Location.File), reference.Location.Line, reference.Location.Column);
            }
            key++;
        }
    }

    public void WriteSymbols(DocumentedSymbol[] symbols, CancellationToken cancellationToken)
    {
        var rows = Command("symbols", "INSERT INTO symbols VALUES($o,$id,$name,$kind,$tk,$sig,$project,$ns,$type,$file,$line,$col,$locations)");
        var documentation = Command("documentation", "INSERT INTO documentation VALUES($o,$status,$value)");
        for (var i = 0; i < symbols.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbol = symbols[i];
            Run(rows, i, symbol.Id, symbol.Name, symbol.Kind, symbol.TypeKind, symbol.Signature, symbol.Project,
                symbol.Namespace, symbol.ContainingType, FileId(symbol.Location.File), symbol.Location.Line,
                symbol.Location.Column, Serialize(symbol.Locations));
            Run(documentation, i, symbol.Documentation.Status, Serialize(symbol.Documentation));
        }
    }

    public void WriteStructuralCatalog(StructuralCatalog catalog, CancellationToken cancellationToken)
    {
        var facts = Command("facts", "INSERT INTO facts VALUES($o,$owner,$kind,$tn,$ts,$td,$tsid,$ta,$tr,$origin,$file,$sl,$sc,$efile,$el,$ec,$scope)");
        for (var i = 0; i < catalog.Facts.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fact = catalog.Facts[i];
            var target = fact.Target;
            var range = fact.Evidence.Location;
            Run(facts, i, fact.OwnerId, fact.Kind, target.Name, target.Signature, target.DeclarationId,
                target.SourceSymbolId, target.AssemblyIdentity, target.Resolution, fact.Evidence.Origin,
                FileId(range.Start.File), range.Start.Line, range.Start.Column,
                FileId(range.End.File), range.End.Line, range.End.Column, fact.Evidence.SemanticScope);
        }
        var issues = Command("structural_issues", "INSERT INTO structural_issues VALUES($o,$owner,$code,$file,$value)");
        for (var i = 0; i < catalog.Issues.Length; i++)
        {
            var issue = catalog.Issues[i];
            Run(issues, i, issue.OwnerId, issue.Code, FileId(issue.Location.Start.File), Serialize(issue));
        }
    }

    public void WriteSearchIndex(DocumentedSymbol[] symbols, StructuralCatalog catalog, CancellationToken cancellationToken)
    {
        var tokenIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var tokens = Command("search_tokens", "INSERT INTO search_tokens VALUES($id,$token)");
        var postings = Command("search_postings", "INSERT INTO search_postings VALUES($token,$symbol,$name)");
        var totals = PurposeSearch.BuildStoreIndex(symbols, catalog, (ordinal, symbolTokens) =>
        {
            foreach (var (token, nameField) in symbolTokens)
            {
                if (!tokenIds.TryGetValue(token, out var id))
                {
                    tokenIds.Add(token, id = tokenIds.Count + 1);
                    Run(tokens, id, token);
                }
                Run(postings, id, ordinal, nameField ? 1 : 0);
            }
        }, cancellationToken);
        var lengths = Command("search_field_lengths", "INSERT INTO search_field_lengths VALUES($kind,$family,$total,$count)");
        foreach (var (kind, families) in totals)
            foreach (var (family, total) in families)
                Run(lengths, kind, family, total.Length, total.Count);
    }

    public void WriteDuplicates(DuplicateCatalog catalog, CancellationToken cancellationToken)
    {
        var groups = Command("duplicate_groups", "INSERT INTO duplicate_groups VALUES($o,$value)");
        for (var i = 0; i < catalog.Groups.Length; i++)
            Run(groups, i, Serialize(catalog.Groups[i]));
        if (catalog.Analysis is { } analysis)
        {
            var vocabulary = Command("duplicate_vocabulary", "INSERT INTO duplicate_vocabulary VALUES($o,$token)");
            for (var i = 0; i < analysis.Vocabulary.Length; i++)
                Run(vocabulary, i, analysis.Vocabulary[i]);
            var bodies = Command("duplicate_bodies", "INSERT INTO duplicate_bodies VALUES($o,$symbol,$file,$count,$value)");
            for (var i = 0; i < analysis.Bodies.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var body = analysis.Bodies[i];
                Run(bodies, i, body.SymbolId, FileId(body.Location.Start.File), body.Tokens.Length, Serialize(body));
            }
            var omissions = Command("duplicate_body_omissions", "INSERT INTO duplicate_body_omissions VALUES($o,$symbol,$value)");
            for (var i = 0; i < analysis.Omissions.Length; i++)
                Run(omissions, i, analysis.Omissions[i].SymbolId, Serialize(analysis.Omissions[i]));
        }
        if (catalog.Scan is { } scan)
        {
            var candidates = Command("duplicate_candidates", "INSERT INTO duplicate_candidates VALUES($o,$left,$right,$lp,$rp,$value)");
            for (var i = 0; i < scan.Candidates.Length; i++)
            {
                var candidate = scan.Candidates[i];
                Run(candidates, i, candidate.Left.SymbolId, candidate.Right.SymbolId, candidate.Left.Project,
                    candidate.Right.Project, Serialize(candidate));
            }
            var omissions = Command("duplicate_scan_omissions", "INSERT INTO duplicate_scan_omissions VALUES($o,$symbol,$value)");
            for (var i = 0; i < scan.Omissions.Length; i++)
                Run(omissions, i, scan.Omissions[i].SymbolId, Serialize(scan.Omissions[i]));
        }
    }

    public void WriteInputs(CodebaseIndex index, CancellationToken cancellationToken)
    {
        if (index.InputManifest is { } manifest)
        {
            var files = Command("input_files", "INSERT INTO input_files VALUES($o,$path,$present,$sha)");
            var i = 0;
            foreach (var (path, fingerprint) in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Run(files, i++, path, fingerprint.Exists ? 1 : 0, fingerprint.Sha256);
            }
        }
        var fingerprints = Command("fingerprints", "INSERT INTO fingerprints VALUES($o,$path,$sha)");
        var ordinal = 0;
        foreach (var (path, hash) in index.Fingerprints)
            Run(fingerprints, ordinal++, path, hash);
    }

    public void WriteIncrementalState(IncrementalSnapshotState state)
    {
        var projects = Command("incremental_projects", "INSERT INTO incremental_projects VALUES($o,$project,$hash)");
        var trees = Command("incremental_trees", "INSERT INTO incremental_trees VALUES($p,$o,$key,$project,$origin,$physical,$searchable,$content,$surface,$parse)");
        for (var p = 0; p < state.Projects.Length; p++)
        {
            var project = state.Projects[p];
            Run(projects, p, project.Project, project.ContextHash);
            for (var t = 0; t < project.Trees.Length; t++)
            {
                var tree = project.Trees[t];
                Run(trees, p, t, tree.Key, tree.Project, tree.OriginPath, tree.PhysicalPath, tree.Searchable ? 1 : 0,
                    tree.ContentHash, tree.SurfaceHash, tree.ParseOptionsHash);
            }
        }
    }

    /// <summary>Indexes, verifies and marks the database complete. Nothing is written afterwards.</summary>
    public void Complete(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Commit();
        Execute(connection, SqliteIndexSchema.Indexes);
        Execute(connection, "ANALYZE;");
        cancellationToken.ThrowIfCancellationRequested();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check";
            var result = check.ExecuteScalar() as string;
            if (result != "ok")
                throw new IOException("The new index database failed its integrity check: " + result);
        }
        Execute(connection, $"PRAGMA user_version={SqliteIndexSchema.StoreVersion};");
        completed = true;
    }

    public void Dispose()
    {
        foreach (var command in commands.Values) command.Dispose();
        commands.Clear();
        if (!completed) transaction?.Dispose();
        transaction = null;
        connection.Dispose();
    }

    private long FileId(string path)
    {
        if (fileIds.TryGetValue(path, out var id)) return id;
        id = fileIds.Count + 1;
        fileIds.Add(path, id);
        Run(Command("files", "INSERT INTO files VALUES($id,$path,$hash)"), id, path, contentHashes.GetValueOrDefault(path));
        return id;
    }

    private void Meta(string key, object? value) =>
        Run(Command("meta", "INSERT INTO meta VALUES($key,$value)"), key, Serialize(value));

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, SqliteIndexSchema.Json);

    private SqliteCommand Command(string name, string text)
    {
        if (commands.TryGetValue(name, out var command)) return command;
        command = connection.CreateCommand();
        command.CommandText = text;
        command.Transaction = transaction;
        // Values are bound positionally, in placeholder order.
        foreach (var match in System.Text.RegularExpressions.Regex.Matches(text, @"\$[a-z]+").Cast<System.Text.RegularExpressions.Match>())
            command.Parameters.Add(new SqliteParameter { ParameterName = match.Value });
        command.Prepare();
        commands.Add(name, command);
        return command;
    }

    private void Run(SqliteCommand command, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
            command.Parameters[i].Value = values[i] ?? DBNull.Value;
        command.ExecuteNonQuery();
        if (++pending >= BatchRows)
        {
            Commit();
            Begin();
        }
    }

    private void Begin()
    {
        transaction = connection.BeginTransaction();
        foreach (var command in commands.Values) command.Transaction = transaction;
    }

    private void Commit()
    {
        transaction?.Commit();
        transaction?.Dispose();
        transaction = null;
        pending = 0;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

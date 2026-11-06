using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CodeAtlas.Explanations;

/// <summary>
/// No workspace/MSBuild/provider load. The symbol catalog MUST be the persisted M6 catalog
/// from this exact LoadedSnapshot; the integration adapter must not obtain a second catalog.
/// </summary>
public sealed class ExplanationRequestBuilder
{
    private readonly LoadedSnapshot snapshot;
    private readonly IReadOnlyList<DocumentedSymbol> symbols;
    internal ExplanationRequestBuilder(LoadedSnapshot snapshot, IReadOnlyList<DocumentedSymbol> symbols)
    {
        this.snapshot = snapshot;
        this.symbols = symbols;
    }
    internal Func<string, CancellationToken, Task>? Checkpoint { get; init; }
    // Only ExplanationSnapshotAdapter exposes construction to production callers.
    public async Task<ExplanationRequest> BuildAsync(string symbolId, ExplanationLimits limits,
        string[]? suppliedContextFiles = null, string? localName = null, int referenceLimit = 2,
        CancellationToken cancellationToken = default)
    {
        limits.Validate();
        if (referenceLimit is < 0 or > 8) throw new QueryException("invalid_arguments", "Reference context limit is 0 through 8.");
        if (!snapshot.VerifiedGeneration || snapshot.Index.InputManifest is null)
            throw new QueryException("rebuild_required", "Explanations require a verified M4 generation and M6 symbol catalog.");
        var symbol = symbols.SingleOrDefault(s => s.Id == symbolId)
            ?? throw new QueryException("symbol_not_found", "Select an ID from this snapshot's symbol inventory.");
        if (symbol.Kind is not ("method" or "type" or "property" or "field"))
            throw new QueryException("unsupported_symbol_kind", "This symbol kind has no explanation context adapter.");
        if (localName is not null && (symbol.Kind != "method" || string.IsNullOrWhiteSpace(localName) || localName.Length > 128))
            throw new QueryException("invalid_arguments", "A local name requires a selected supported method.");
        await IndexStore.RequireFreshAsync(snapshot.Index, cancellationToken);
        if (Checkpoint is { } beforeRead) await beforeRead("after-initial-freshness", cancellationToken);

        var root = Path.GetDirectoryName(snapshot.Index.Solution)!;
        var files = new Dictionary<string, (string Text, string Hash, bool Tracked)>(StringComparer.Ordinal);
        var evidence = new List<ExplanationEvidence>();
        var facts = new List<ExplanationFact>();
        var locals = new List<ExplanationLocal>();
        var sourceEvidence = new List<string>();

        async Task<string> Read(string relative, bool tracked)
        {
            if (files.TryGetValue(relative, out var previous))
            {
                if (tracked && !previous.Tracked) throw ExplanationContract.Invalid("A supplied context file cannot become an unverified source input.");
                return previous.Text;
            }
            if (files.Count >= limits.MaxFiles) throw new QueryException("explanation_budget_exceeded", "Too many context files; reduce explicit context/reference limits.");
            string path;
            try { path = ExplanationFiles.InsideRoot(root, relative); }
            catch (QueryException exception) when (tracked && exception.Code == "invalid_explanation")
            {
                throw new QueryException("unsupported_explanation_context", "Indexed source is outside the supported root/path model; no external source was opened.");
            }
            var manifest = snapshot.Index.InputManifest!;
            // Verify the bytes read against the EXACT already-loaded manifest, not a re-resolved current.json.
            var expected = manifest.Files.FirstOrDefault(p => SamePath(p.Key, path)).Value;
            if (tracked && (expected is null || !expected.Exists || expected.Sha256 is null))
                throw new QueryException("unsupported_explanation_context", "Source evidence is not a hashed input in this generation.");
            var bytes = await ExplanationFiles.ReadBoundedAsync(path, limits.MaxSourceFileBytes, cancellationToken);
            var hash = ExplanationContract.Hash(bytes);
            if (tracked && !hash.Equals(expected!.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new StaleIndexException([relative]);
            // Strict UTF-8 with optional UTF-8 BOM. Do not silently decode another encoding into misleading coordinates.
            var offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
            var text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            files.Add(relative, (text, hash, tracked));
            return text;
        }

        string Add(string file, string text, int start, int length, string role)
        {
            if (start < 0 || length < 1 || start > text.Length - length)
                throw ExplanationContract.Invalid("Source evidence range does not exist in the verified file.");
            var existing = evidence.FirstOrDefault(item => item.File == file && item.StartOffset == start && item.Text.Length == length);
            if (existing is not null) return existing.Id;
            var source = SourceText.From(text);
            var id = "e" + (evidence.Count + 1);
            evidence.Add(new(id, file, start, source.Lines.GetLineFromPosition(start).LineNumber + 1,
                source.Lines.GetLineFromPosition(start + length - 1).LineNumber + 1, text.Substring(start, length), role));
            return id;
        }

        foreach (var position in symbol.Locations.Distinct().OrderBy(p => p.File, StringComparer.Ordinal)
                     .ThenBy(p => p.Line).ThenBy(p => p.Column))
        {
            var text = await Read(position.File, tracked: true);
            var node = FindDeclaration(text, position, symbol, cancellationToken);
            var e = Add(position.File, text, node.SpanStart, node.Span.Length, "selected-declaration");
            sourceEvidence.Add(e);
            if (localName is not null && node is MethodDeclarationSyntax method)
            {
                var candidates = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .Where(v => v.Identifier.ValueText == localName
                        && !v.Ancestors().TakeWhile(a => a != method).Any(a => a is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
                    .ToArray();
                foreach (var candidate in candidates)
                    locals.Add(new(localName, e, candidate.Identifier.SpanStart - node.SpanStart, candidate.Identifier.Span.Length));
            }
        }
        if (sourceEvidence.Count == 0) throw new QueryException("unsupported_explanation_context", "No selected declaration is available.");
        if (localName is not null && locals.Count != 1)
            throw new QueryException("unsupported_explanation_context", "Local name must identify exactly one declaration in the selected method; nested functions and non-declarator locals are unsupported.");
        facts.Add(new("selected-symbol", $"{symbol.Kind}: {symbol.Signature}; project: {symbol.Project}", sourceEvidence.ToArray()));

        // Author XML and inherited XML stay in DeveloperDocumentation. Every source comment used
        // by inheritance is separately included with source evidence; no include/cref file is opened.
        var docSources = symbol.Documentation.Comments.Select(c => (Owner: symbol.Id, Comment: c.Id))
            .Concat(symbol.Documentation.Sections.Select(s => (Owner: s.Evidence.SourceSymbolId, Comment: s.Evidence.CommentId)))
            .Distinct().OrderBy(x => x.Owner, StringComparer.Ordinal).ThenBy(x => x.Comment, StringComparer.Ordinal);
        foreach (var item in docSources)
        {
            var owner = symbols.SingleOrDefault(s => s.Id == item.Owner)
                ?? throw ExplanationContract.Invalid("Documentation source is missing from the loaded catalog.");
            var comment = owner.Documentation.Comments.SingleOrDefault(c => c.Id == item.Comment)
                ?? throw ExplanationContract.Invalid("Documentation comment is missing from the loaded catalog.");
            if (comment.Location.Start.File != comment.Location.End.File)
                throw ExplanationContract.Invalid("Documentation evidence crosses files.");
            var text = await Read(comment.Location.Start.File, tracked: true);
            var start = Offset(text, comment.Location.Start);
            var end = Offset(text, comment.Location.End);
            Add(comment.Location.Start.File, text, start, end - start, "developer-xml-source");
        }

        var references = snapshot.Index.References.GetValueOrDefault(symbol.Id) ?? [];
        foreach (var reference in references.Take(referenceLimit))
        {
            var position = reference.Location;
            var text = await Read(position.File, tracked: true);
            var source = SourceText.From(text);
            var point = Offset(text, position);
            var line = source.Lines.GetLineFromPosition(point);
            var id = Add(position.File, text, line.Start, line.Span.Length, "static-reference-site");
            facts.Add(new("static-reference", $"kind={reference.Kind}; referencing project={reference.Project}; definitionId={reference.DefinitionId}", [id]));
        }
        facts.Add(new("reference-coverage", $"Included {Math.Min(referenceLimit, references.Length)} of {references.Length} stored static reference entries. References are not a complete runtime call graph.", sourceEvidence.ToArray()));

        var relations = snapshot.Index.StructuralCatalog?.Facts.Where(fact => fact.OwnerId == symbol.Id).ToArray() ?? [];
        foreach (var relation in relations.Take(32))
        {
            var range = relation.Evidence.Location;
            var text = await Read(range.Start.File, tracked: true);
            var start = Offset(text, range.Start);
            var end = Offset(text, range.End);
            if (range.Start.File != range.End.File || end <= start)
                throw ExplanationContract.Invalid("Structural evidence range is invalid.");
            var evidenceId = Add(range.Start.File, text, start, end - start, "semantic-relation-site");
            facts.Add(new(relation.Kind,
                $"target={relation.Target.Signature}; resolution={relation.Target.Resolution}; sourceSymbolId={relation.Target.SourceSymbolId}", [evidenceId]));
        }
        facts.Add(new("relation-coverage", $"Included {Math.Min(32, relations.Length)} of {relations.Length} selected-symbol structural relations. Bindings do not establish business intent or complete runtime dispatch.", sourceEvidence.ToArray()));

        foreach (var relative in (suppliedContextFiles ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            // Only user-selected, root-contained files; plain content, no discovery or recursive inclusion.
            var text = await Read(relative, tracked: false);
            if (text.Length == 0) throw ExplanationContract.Invalid("Supplied context is empty.");
            Add(relative, text, 0, text.Length, "user-supplied-context");
        }
        var contextBytes = evidence.Sum(e => (long)Encoding.UTF8.GetByteCount(e.Text));
        if (contextBytes > limits.MaxContextBytes)
            throw new QueryException("explanation_budget_exceeded", "Context exceeds the byte budget; no source was silently truncated.");
        var inputs = files.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new ExplanationInput(
            p.Key, p.Value.Hash, p.Value.Tracked ? "snapshot-source" : "user-supplied-context", p.Value.Tracked)).ToArray();
        var request = new ExplanationRequest(1, Guid.NewGuid().ToString("N"), snapshot.Index.IndexId, "",
            DependencyFingerprint(snapshot.Index), ExplanationContract.GenerationVersion, DateTimeOffset.UtcNow,
            new(symbol.Id, symbol.Name, symbol.Kind, symbol.Signature, symbol.Project, localName),
            symbol.Documentation, facts.ToArray(), evidence.ToArray(), inputs, locals.ToArray(), limits, ExplanationContract.Instructions);
        request = request with { ContextFingerprint = ExplanationContract.ContextHash(request) };
        var serialized = JsonSerializer.SerializeToUtf8Bytes(request, ExplanationContract.Json);
        // Approximation only: no tokenizer/provider/cost guarantee. The consuming agent must impose
        // its own true context/output/token/billing limits before any model execution.
        if (serialized.Length > limits.MaxRequestBytes || (serialized.Length + 2L) / 3 > limits.MaxEstimatedTokens)
            throw new QueryException("explanation_budget_exceeded", "Serialized request exceeds its byte or approximate token budget.");

        // Closes precheck -> source-read race: also recheck external supplied files after all reads.
        if (Checkpoint is { } afterRead) await afterRead("before-final-freshness", cancellationToken);
        await VerifyInputsAsync(snapshot, request, cancellationToken);
        return request;
    }

    internal static string DependencyFingerprint(CodebaseIndex index)
    {
        var m = index.InputManifest ?? throw ExplanationContract.Invalid("Missing input manifest.");
        // Versioned complete dependency set, including absent candidates, discovery and environment.
        // Serialization order is fixed explicitly for dictionaries to avoid incidental key order.
        return ExplanationContract.Hash(new
        {
            m.InputModelVersion,
            Files = m.Files.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
            Roots = m.DiscoveryRootStates.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
            m.ExplicitFiles, m.DiscoveryScopes, m.DirectoryInventories, m.EnvironmentDigest, m.Evidence, m.Coverage
        });
    }

    internal static async Task<IndexFreshness> VerifyInputsAsync(LoadedSnapshot snapshot, ExplanationRequest request,
        CancellationToken token)
    {
        await VerifyContextInputsAsync(snapshot, request, token);
        var freshness = await IndexStore.CheckFreshnessAsync(snapshot.Index, token);
        if (freshness.IsStale) throw new StaleIndexException(freshness.ChangedFiles);
        if (freshness.Diagnostics.Length != 0)
            throw new IndexFormatException("snapshot_io_failed", "Cannot verify explanation dependencies.", freshness.Diagnostics);
        return freshness; // Preserve unknown/partial; exact observed bytes do not prove complete build coverage.
    }

    // Bulk readers bracket the operation with full freshness checks, while reusing the
    // same context validation and hashing each shared context file only once per pass.
    internal static async Task VerifyContextInputsAsync(LoadedSnapshot snapshot, ExplanationRequest request,
        CancellationToken token, Dictionary<string, (int Length, string Hash)>? verifiedFiles = null,
        string? dependencyFingerprint = null)
    {
        if (!snapshot.VerifiedGeneration || snapshot.Index.InputManifest is null)
            throw new QueryException("rebuild_required", "Explanations require a verified generation.");
        if (request.IndexId != snapshot.Index.IndexId)
            throw new QueryException("snapshot_mismatch", "This explanation request belongs to another generation. Export a new request.");
        if (request.ContextFingerprint != ExplanationContract.ContextHash(request)
            || request.DependencyFingerprint != (dependencyFingerprint ?? DependencyFingerprint(snapshot.Index)))
            throw ExplanationContract.Invalid("Explanation request context/dependency fingerprint mismatch.");
        var root = Path.GetDirectoryName(snapshot.Index.Solution)!;
        foreach (var input in request.Inputs)
        {
            var path = ExplanationFiles.InsideRoot(root, input.File);
            var expected = snapshot.Index.InputManifest.Files.FirstOrDefault(p => SamePath(p.Key, path)).Value;
            var semanticInput = input.SnapshotTracked || expected?.Exists == true;
            (int Length, string Hash) verified;
            if (verifiedFiles is null || !verifiedFiles.TryGetValue(path, out verified))
            {
                byte[] bytes;
                try { bytes = await ExplanationFiles.ReadBoundedAsync(path, request.Limits.MaxSourceFileBytes, token); }
                catch (Exception exception) when (semanticInput && exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    throw new StaleIndexException([input.File]);
                }
                verified = (bytes.Length, ExplanationContract.Hash(bytes));
                verifiedFiles?.Add(path, verified);
            }
            if (verified.Length > request.Limits.MaxSourceFileBytes)
                throw new QueryException("explanation_budget_exceeded", "Context input exceeds this request's source byte budget.");
            var hash = verified.Hash;
            if (hash != input.Sha256)
            {
                if (semanticInput) throw new StaleIndexException([input.File]);
                throw new QueryException("stale_explanation", "Explanation input changed: " + input.File);
            }
            if (input.SnapshotTracked)
            {
                if (expected is null || !expected.Exists || !hash.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new StaleIndexException([input.File]);
            }
        }
    }

    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static int Offset(string text, SourcePosition p)
    {
        var source = SourceText.From(text);
        if (p.Line < 1 || p.Line > source.Lines.Count || p.Column < 1 || p.Column - 1 > source.Lines[p.Line - 1].Span.Length)
            throw ExplanationContract.Invalid("Source position is outside its verified file.");
        return source.Lines[p.Line - 1].Start + p.Column - 1;
    }

    private static SyntaxNode FindDeclaration(string text, SourcePosition position, DocumentedSymbol symbol,
        CancellationToken cancellationToken)
    {
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: cancellationToken);
        var root = tree.GetRoot(cancellationToken);
        // Current persisted catalog has no parse options. Refuse conditional directives rather than silently
        // using another target/preprocessor branch. A future persisted per-project parse context
        // may replace this conservative adapter after its own matching-context tests.
        if (root.DescendantTrivia(descendIntoTrivia: true).Any(t => t.GetStructure() is
                IfDirectiveTriviaSyntax or ElifDirectiveTriviaSyntax or ElseDirectiveTriviaSyntax
                or EndIfDirectiveTriviaSyntax or DefineDirectiveTriviaSyntax or UndefDirectiveTriviaSyntax)
            || tree.GetDiagnostics(cancellationToken).Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new QueryException("unsupported_explanation_context", "Conditional/directive-defined or syntactically unsupported source needs recorded parse context; no context was guessed.");
        var offset = Offset(text, position);
        var token = root.FindToken(offset);
        if (token.SpanStart != offset)
            throw ExplanationContract.Invalid("Indexed declaration start does not match a source token.");
        bool MatchesName(string name, bool explicitlyImplemented) => name == symbol.Name
            || explicitlyImplemented && symbol.Name.EndsWith("." + name, StringComparison.Ordinal);
        SyntaxNode? node = symbol.Kind switch
        {
            "method" => token.Parent?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>()
                .SingleOrDefault(n => n.Identifier == token && MatchesName(n.Identifier.ValueText, n.ExplicitInterfaceSpecifier is not null)),
            "type" => token.Parent?.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>()
                .SingleOrDefault(n => n.Identifier == token && n.Identifier.ValueText == symbol.Name)
                ?? (SyntaxNode?)token.Parent?.AncestorsAndSelf().OfType<DelegateDeclarationSyntax>()
                    .SingleOrDefault(n => n.Identifier == token && n.Identifier.ValueText == symbol.Name),
            "property" => token.Parent?.AncestorsAndSelf().OfType<PropertyDeclarationSyntax>()
                .SingleOrDefault(n => n.Identifier == token && MatchesName(n.Identifier.ValueText, n.ExplicitInterfaceSpecifier is not null))
                ?? (SyntaxNode?)token.Parent?.AncestorsAndSelf().OfType<IndexerDeclarationSyntax>()
                    .SingleOrDefault(n => n.ThisKeyword == token && (MatchesName("this[]", n.ExplicitInterfaceSpecifier is not null)
                        || MatchesName("Item", n.ExplicitInterfaceSpecifier is not null))),
            "field" => token.Parent?.AncestorsAndSelf().OfType<VariableDeclaratorSyntax>()
                .SingleOrDefault(n => n.Identifier == token && n.Identifier.ValueText == symbol.Name && n.Parent?.Parent is FieldDeclarationSyntax)
                ?? (SyntaxNode?)token.Parent?.AncestorsAndSelf().OfType<EnumMemberDeclarationSyntax>()
                    .SingleOrDefault(n => n.Identifier == token && n.Identifier.ValueText == symbol.Name),
            _ => null
        };
        if (node is null)
            throw new QueryException("unsupported_explanation_context", "The indexed kind/name/start cannot be matched exactly in verified syntax.");
        // A field declarator alone omits its declared type/modifiers; include its owning field declaration.
        return node is VariableDeclaratorSyntax ? node.Parent!.Parent! : node;
    }
}

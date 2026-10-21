using System.Text.Json;

namespace CodeAtlas.Explanations;

/// <summary>
/// Optional, explicit derived state. Nothing here is called by indexing, XML/fact/search queries,
/// or the M4 publisher. A caller loads one snapshot before constructing this service.
/// </summary>
public sealed class ExplanationService
{
    private readonly LoadedSnapshot snapshot;
    private readonly IReadOnlyList<DocumentedSymbol> symbols;
    private readonly string home;
    internal const int MaximumRequestBytes = 524_288;

    internal ExplanationService(LoadedSnapshot snapshot, IReadOnlyList<DocumentedSymbol> symbols)
    {
        this.snapshot = snapshot;
        this.symbols = symbols;
        var root = Path.GetDirectoryName(snapshot.Index.Solution)!;
        var solutionKey = Path.GetFileName(Path.GetDirectoryName(IndexStore.DefaultPath(snapshot.Index.Solution)))!;
        home = ExplanationFiles.InsideRoot(root, ".codeatlas/explanations/" + solutionKey);
    }

    public async Task<ExplanationRequest> ExportAsync(string symbolId, string output,
        ExplanationLimits? limits = null, string[]? suppliedContextFiles = null, string? localName = null,
        int referenceLimit = 2, CancellationToken cancellationToken = default)
    {
        var request = await new ExplanationRequestBuilder(snapshot, symbols).BuildAsync(symbolId,
            limits ?? new(), suppliedContextFiles, localName, referenceLimit, cancellationToken);
        output = Path.GetFullPath(output);
        var snapshots = Path.GetDirectoryName(Path.GetDirectoryName(IndexStore.DefaultPath(snapshot.Index.Solution)))!;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!output.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || output.StartsWith(snapshots + Path.DirectorySeparatorChar, comparison)
            || output.StartsWith(home + Path.DirectorySeparatorChar, comparison))
            throw new QueryException("invalid_arguments", "Export to a new .json file outside snapshot and managed explanation storage.");
        // Registry commits first; failed external export may leave a harmless unused canonical request.
        // The external copy is never trusted at import; editing it cannot change registered evidence.
        await ExplanationFiles.WriteCheckedAsync(RequestPath(request.RequestId), request, cancellationToken);
        await ExplanationRequestBuilder.VerifyInputsAsync(snapshot, request, cancellationToken);
        await ExplanationFiles.AtomicWriteAsync(output, JsonSerializer.SerializeToUtf8Bytes(request,
            ExplanationContract.Json), cancellationToken);
        return request;
    }

    public async Task<AcceptedExplanation> AcceptAsync(string responseFile, CancellationToken cancellationToken = default)
    {
        try { return await AcceptResponseAsync(responseFile, cancellationToken); }
        catch (JsonException)
        {
            throw ExplanationContract.Invalid("The response is not valid strict explanation JSON.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new QueryException("explanation_io_failed", "The explanation response or local derived storage could not be accessed. Check its path, permissions and free space.");
        }
    }

    private async Task<AcceptedExplanation> AcceptResponseAsync(string responseFile, CancellationToken cancellationToken)
    {
        // Absolute hard bound applies before we know a request's more restrictive limit.
        var bytes = await ExplanationFiles.ReadBoundedAsync(responseFile, 262_144, cancellationToken);
        var response = ExplanationFiles.Parse<ExplanationResponse>(bytes);
        if (!Guid.TryParseExact(response.RequestId, "N", out _))
            throw ExplanationContract.Invalid("Response request identity must be a registered request ID.");
        ExplanationRequest request;
        try { request = await LoadRequestAsync(response.RequestId, cancellationToken); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new QueryException("explanation_request_not_found", "No registered local request has this identity.");
        }
        if (bytes.Length > request.Limits.MaxResponseBytes)
            throw new QueryException("explanation_budget_exceeded", "Response exceeds this request's byte budget.");
        ValidateResponse(request, response);
        await ExplanationRequestBuilder.VerifyInputsAsync(snapshot, request, cancellationToken);
        var accepted = new AcceptedExplanation("ai-interpretation", request.RequestId,
            request.ContextFingerprint, snapshot.Index.IndexId, DateTimeOffset.UtcNow,
            "responder-reported-not-authenticated", response);
        // Serialize concurrent imports for this one derived entry. Ordinary readers need no lock.
        var entry = EntryPath(request.RequestId);
        Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
        var pointerPath = SymbolPointerPath(request.Selection.SymbolId);
        Directory.CreateDirectory(Path.GetDirectoryName(pointerPath)!);
        using var symbolLock = AcquireSymbolLock(pointerPath + ".lock");
        using var writerLock = new FileStream(entry + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        cancellationToken.ThrowIfCancellationRequested();
        await ExplanationRequestBuilder.VerifyInputsAsync(snapshot, request, cancellationToken);
        await ExplanationFiles.WriteCheckedAsync(entry, accepted, cancellationToken, replace: true);
        await ExplanationFiles.WriteCheckedAsync(pointerPath,
            new SymbolPointer(request.Selection.SymbolId, request.RequestId), CancellationToken.None, replace: true);
        return accepted;
    }

    private sealed record SymbolPointer(string SymbolId, string RequestId);

    private static FileStream AcquireSymbolLock(string path)
    {
        try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33
            || !OperatingSystem.IsWindows() && (exception.HResult & 0xFFFF) == 11)
        {
            throw new QueryException("explanation_busy", "Another response is being imported for this symbol. Retry when it finishes.");
        }
    }

    private string SymbolPointerPath(string symbolId) => Path.Combine(home, "symbols",
        ExplanationContract.Hash(System.Text.Encoding.UTF8.GetBytes(symbolId)) + ".json");

    public async Task<ExplanationView> GetForSymbolAsync(string symbolId, CancellationToken cancellationToken = default)
    {
        if (!symbols.Any(symbol => symbol.Id == symbolId))
            throw new QueryException("symbol_not_found", "Source symbol ID was not found in this snapshot.");
        var freshness = await IndexStore.CheckFreshnessAsync(snapshot.Index, cancellationToken);
        if (freshness.IsStale) throw new StaleIndexException(freshness.ChangedFiles);
        if (freshness.Diagnostics.Length != 0)
            throw new IndexFormatException("snapshot_io_failed", "Cannot verify explanation dependencies.", freshness.Diagnostics);
        try
        {
            var pointer = await ExplanationFiles.ReadCheckedAsync<SymbolPointer>(SymbolPointerPath(symbolId), 4096, cancellationToken);
            if (pointer.SymbolId != symbolId) throw ExplanationContract.Invalid("Explanation symbol pointer mismatch.");
            ExplanationContract.RequireId(pointer.RequestId);
            var registered = await ExplanationFiles.ReadCheckedAsync<ExplanationRequest>(RequestPath(pointer.RequestId), MaximumRequestBytes, cancellationToken);
            ValidateRequest(registered, pointer.RequestId);
            if (registered.Selection.SymbolId != symbolId)
                throw ExplanationContract.Invalid("Registered request belongs to another symbol.");
            return await GetAsync(pointer.RequestId, cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new("missing", "", snapshot.Index.IndexId, "No accepted explanation for this symbol.", null, freshness);
        }
        catch (Exception exception) when (CacheFailure(exception))
        {
            return new("cache-unavailable", "", snapshot.Index.IndexId,
                "Explanation symbol pointer is corrupt or unreadable; import again.", null, freshness);
        }
    }

    public async Task<ExplanationView> GetAsync(string requestId, CancellationToken cancellationToken = default)
    {
        ExplanationContract.RequireId(requestId);
        var freshness = await IndexStore.CheckFreshnessAsync(snapshot.Index, cancellationToken);
        if (freshness.IsStale)
            throw new StaleIndexException(freshness.ChangedFiles); // Existing query refusal remains intact.
        if (freshness.Diagnostics.Length != 0)
            throw new IndexFormatException("snapshot_io_failed", "Cannot verify explanation dependencies.", freshness.Diagnostics);
        ExplanationRequest request;
        try { request = await LoadRequestAsync(requestId, cancellationToken); }
        catch (FileNotFoundException) { return View("missing", "No registered request in this generation."); }
        catch (DirectoryNotFoundException) { return View("missing", "No registered request in this generation."); }
        catch (QueryException e) when (e.Code == "snapshot_mismatch") { return View("stale", e.Message); }
        catch (Exception e) when (CacheFailure(e)) { return View("cache-unavailable", "Registered explanation request is corrupt or unreadable; export again."); }
        try
        {
            freshness = await ExplanationRequestBuilder.VerifyInputsAsync(snapshot, request, cancellationToken);
        }
        catch (QueryException e) when (e.Code == "stale_explanation") { return View("stale", e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return View("unverifiable", "Cannot re-read an explanation context input.");
        }
        try
        {
            var entry = await ExplanationFiles.ReadCheckedAsync<AcceptedExplanation>(EntryPath(requestId), 524_288, cancellationToken);
            if (entry.RequestId != requestId || entry.IndexId != snapshot.Index.IndexId
                || entry.ContextFingerprint != request.ContextFingerprint || entry.Origin != "ai-interpretation"
                || entry.ProvenanceTrust != "responder-reported-not-authenticated")
                throw ExplanationContract.Invalid("Explanation cache identity mismatch.");
            ValidateResponse(request, entry.Response);
            return new("current-for-observed-inputs", requestId, snapshot.Index.IndexId, null, entry, freshness);
        }
        catch (FileNotFoundException) { return View("missing", "Request exists but no response has been accepted."); }
        catch (DirectoryNotFoundException) { return View("missing", "Request exists but no response has been accepted."); }
        catch (Exception e) when (CacheFailure(e)) { return View("cache-unavailable", "Explanation cache is corrupt or unreadable; re-import a verified response."); }

        ExplanationView View(string state, string reason) => new(state, requestId, snapshot.Index.IndexId,
            reason, null, freshness);
    }

    private static bool CacheFailure(Exception e) => e is IOException or UnauthorizedAccessException
        or JsonException or QueryException or ArgumentException or InvalidOperationException;

    private async Task<ExplanationRequest> LoadRequestAsync(string requestId, CancellationToken token)
    {
        var request = await ExplanationFiles.ReadCheckedAsync<ExplanationRequest>(RequestPath(requestId), MaximumRequestBytes, token);
        ValidateRequest(request, requestId);
        if (request.IndexId != snapshot.Index.IndexId)
            throw new QueryException("snapshot_mismatch", "Request belongs to another index generation; export again.");
        if (!symbols.Any(s => s.Id == request.Selection.SymbolId))
            throw ExplanationContract.Invalid("Request symbol is not in this generation.");
        return request;
    }

    // Request IDs are the only response-controlled component used to form any path.
    private string RequestPath(string id)
    {
        ExplanationContract.RequireId(id);
        return Path.Combine(home, "requests", id + ".json");
    }
    private string EntryPath(string id)
    {
        ExplanationContract.RequireId(id);
        return Path.Combine(home, "entries", snapshot.Index.IndexId, id + ".json");
    }

    internal static void ValidateRequest(ExplanationRequest r, string expectedId)
    {
        if (r is null || r.ContractVersion != 1 || r.RequestId != expectedId
            || !Guid.TryParseExact(r.IndexId, "N", out _) || r.GenerationVersion != ExplanationContract.GenerationVersion
            || r.Instructions != ExplanationContract.Instructions || r.Limits is null || r.Selection is null
            || string.IsNullOrWhiteSpace(r.Selection.SymbolId) || r.DeveloperDocumentation is null
            || r.Evidence is null || r.Inputs is null || r.StructuralFacts is null || r.Locals is null
            || r.Evidence.Length is < 1 or > 512 || r.Inputs.Length is < 1 or > 16
            || r.Evidence.Any(e => e is null || string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.File)
                || e.StartOffset < 0 || e.StartLine < 1 || e.EndLine < e.StartLine || string.IsNullOrEmpty(e.Text))
            || r.Inputs.Any(i => i is null || string.IsNullOrWhiteSpace(i.File) || i.Sha256 is not { Length: 64 }
                || !i.Sha256.All(Uri.IsHexDigit))
            || r.Evidence.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != r.Evidence.Length
            || r.Inputs.Select(i => i.File).Distinct(StringComparer.Ordinal).Count() != r.Inputs.Length
            || r.Evidence.Any(e => !r.Inputs.Any(i => i.File == e.File))
            || r.ContextFingerprint != ExplanationContract.ContextHash(r))
            throw ExplanationContract.Invalid("Invalid registered explanation request.");
        r.Limits.Validate();
        if (r.Inputs.Length > r.Limits.MaxFiles)
            throw ExplanationContract.Invalid("Registered request exceeds file budget.");
    }

    internal static void ValidateResponse(ExplanationRequest request, ExplanationResponse response)
    {
        if (response is null || response.ContractVersion != 1 || response.RequestId != request.RequestId
            || response.IndexId != request.IndexId || response.SymbolId != request.Selection.SymbolId
            || response.ContextFingerprint != request.ContextFingerprint || response.Provenance is null
            || !Text(response.Provenance.Provider, 200) || !Text(response.Provenance.Model, 200)
            || !Text(response.Provenance.GenerationVersion, 200) || response.Provenance.GeneratedAtUtc == default
            || response.Claims is null || response.Claims.Length is < 1 or > 32
            || response.Limitations is null || response.Limitations.Length > 16
            || response.Limitations.Any(l => !Text(l, 2048)))
            throw ExplanationContract.Invalid("Response identity, provenance or bounded content is invalid.");
        foreach (var claim in response.Claims)
        {
            if (claim is null || !Text(claim.Text, 4096) || claim.Category is not ("source-observation" or "interpretation")
                || claim.Evidence is null || claim.Evidence.Length is < 1 or > 16)
                throw ExplanationContract.Invalid("Each generated claim requires bounded text/category and source evidence.");
            foreach (var citation in claim.Evidence)
            {
                if (citation is null) throw ExplanationContract.Invalid("Null evidence citation.");
                var evidence = request.Evidence.SingleOrDefault(e => e.Id == citation.EvidenceId);
                if (evidence is null || citation.Offset < 0 || citation.Length < 1
                    || citation.Offset > evidence.Text.Length - citation.Length
                    || citation.Quote != evidence.Text.Substring(citation.Offset, citation.Length))
                    throw ExplanationContract.Invalid("Unknown/out-of-bounds evidence or mismatched source quote.");
                if (request.Selection.LocalName is not null && !request.Locals.Any(l => l.Name == request.Selection.LocalName))
                    throw ExplanationContract.Invalid("Requested local has no verified method context.");
            }
        }
        // Citation validity establishes traceability, not that the prose follows logically from the quote.
        // This intentionally makes no automatic accuracy/intent/behavior-equivalence judgement.
        static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
    }
}

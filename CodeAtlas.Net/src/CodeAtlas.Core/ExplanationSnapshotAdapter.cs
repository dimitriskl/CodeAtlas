namespace CodeAtlas.Explanations;

// Construct from the exact already-resolved semantic generation.
// This is intentionally the only public construction path. It cannot take an arbitrary
// symbol catalog from another workspace/generation or rebuild developer text from source.
public static class ExplanationSnapshotAdapter
{
    public static ExplanationService ForSnapshot(LoadedSnapshot snapshot)
    {
        if (!snapshot.VerifiedGeneration || snapshot.Index.InputManifest is null
            || snapshot.Index.DocumentationCoverage?.Version != DocumentationCoverage.Current.Version
            || snapshot.Index.Symbols is not { } symbols)
            throw new QueryException("rebuild_required", "Build a verified snapshot with the supported documentation catalog before requesting an explanation.");
        return new ExplanationService(snapshot, symbols);
    }
}

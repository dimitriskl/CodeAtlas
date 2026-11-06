namespace CodeAtlas.Tests;

internal static class SnapshotTestIdentity
{
    // Tests that republish identical analysis still create a coherent new generation.
    // Production readers and publishers never repair identity mismatches automatically.
    internal static CodebaseIndex NewGeneration(CodebaseIndex index)
    {
        var id = Guid.NewGuid().ToString("N");
        return index with
        {
            IndexId = id,
            IncrementalState = index.IncrementalState is { } state ? state with { IndexId = id } : null,
            Duplicates = index.Duplicates is { Scan: { } scan } duplicates
                ? duplicates with { Scan = scan with { IndexId = id } } : index.Duplicates
        };
    }
}

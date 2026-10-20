namespace CodeAtlas;

public enum IndexStage
{
    ScanningInputs,
    LoadingProjects,
    ValidatingProjects,
    FingerprintingInputs,
    DiscoveringSymbols,
    FindingReferences,
    CollectingTypes,
    CollectingDocumentation,
    VerifyingInputs,
    Saving,
    Completed
}

/// <summary>Completed counts describe work already finished in this stage, not overall progress.</summary>
public sealed record IndexProgress(IndexStage Stage, int? Completed = null, int? Total = null, string? Detail = null);

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

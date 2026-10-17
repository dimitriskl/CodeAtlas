using System.Collections.Concurrent;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;

namespace CodeAtlas;

// Roslyn currently wraps MSBuild warnings as WorkspaceDiagnosticKind.Failure.
// Only reclassify diagnostics corroborated by an actual MSBuild warning event.
internal sealed class BuildDiagnostics : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("codeatlas-build-");
    private readonly ConcurrentDictionary<string, byte> warnings = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> errors = new();

    public IEnumerable<string> Errors => errors;
    public ILogger Logger => new BinaryLogger { Parameters = Path.Combine(directory.FullName, "load.binlog") };

    public void Read(CancellationToken cancellationToken)
    {
        // The out-of-process Roslyn loader only supports BinaryLogger, not custom loggers.
        var eventSource = new BinaryLogReplayEventSource();
        eventSource.WarningRaised += (_, warning) =>
        {
            warnings.TryAdd(Wrapped(warning.ProjectFile, warning.Message), 0);
            warnings.TryAdd(Wrapped(warning.ProjectFile,
                $"{warning.File}: ({warning.LineNumber}, {warning.ColumnNumber}): {warning.Message}"), 0);
        };
        eventSource.ErrorRaised += (_, error) =>
            errors.Enqueue($"{error.ProjectFile}: {error.Code}: {error.Message}");
        foreach (var file in directory.EnumerateFiles("*.binlog"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            eventSource.Replay(file.FullName, cancellationToken);
        }
    }

    public bool IsWarning(string diagnostic) => warnings.ContainsKey(diagnostic);
    public void Dispose() => directory.Delete(recursive: true);

    // Match the pinned Roslyn wrapper exactly; unknown/localized formats fail closed.
    private static string Wrapped(string? project, string? message) =>
        $"Msbuild failed when processing the file '{project}' with message: {message}";
}

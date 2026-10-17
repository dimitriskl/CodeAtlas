using System.Text;

namespace CodeAtlas.Cli;

internal static class WarningReport
{
    public static async Task<string> SaveAsync(string solution, string? indexId, string[] warnings, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(solution))!, ".codeatlas");
        var path = Path.Combine(directory, "warnings.txt");
        // Refresh a previous report even when the latest successful command has no warnings.
        if (warnings.Length == 0 && !File.Exists(path))
            return path;

        var text = new StringBuilder()
            .AppendLine("CodeAtlas warning report")
            .AppendLine($"Solution: {Path.GetFullPath(solution)}")
            .AppendLine($"Source: {(indexId is null ? "live analysis" : $"index {indexId}")}")
            .AppendLine($"Warning count: {warnings.Length}")
            .AppendLine();
        if (warnings.Length == 0)
            text.AppendLine("No warnings.");
        for (var i = 0; i < warnings.Length; i++)
            text.AppendLine($"{i + 1}. {warnings[i]}").AppendLine();

        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"warnings-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, text.ToString(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
        return path;
    }
}

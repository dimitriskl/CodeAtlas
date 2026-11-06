using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class SqliteFallbackTests
{
    private const string ChildVariable = "CODEATLAS_SYSTEM_SQLITE_CHILD";

    [Fact]
    public async Task MissingBundledNativeAssetProbesSystemSqliteWithoutInstallingAnything()
    {
        // Arrange
        var copy = Directory.CreateTempSubdirectory("codeatlas-system-sqlite-");
        try
        {
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                if (IsBundledNativeLibrary(Path.GetFileName(file))) continue;
                var destination = Path.Combine(copy.FullName, Path.GetRelativePath(AppContext.BaseDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            var assembly = Path.Combine(copy.FullName, Path.GetFileName(typeof(SqliteFallbackTests).Assembly.Location));
            var statusPath = Path.Combine(copy.FullName, "system-sqlite-status.json");
            var start = new ProcessStartInfo("dotnet") { CreateNoWindow = true, UseShellExecute = false };
            foreach (var argument in new[] { "vstest", assembly,
                         "/TestCaseFilter:FullyQualifiedName=CodeAtlas.Tests.SqliteFallbackTests.SystemFallbackChildHost" })
                start.ArgumentList.Add(argument);
            start.Environment[ChildVariable] = statusPath;
            start.Environment.Remove(SqliteAvailability.SimulateVariable);

            // Act
            var result = await ChildProcess.RunAsync(start);

            // Assert
            Assert.True(result.ExitCode == 0, result.Output + result.Error);
            Assert.True(File.Exists(statusPath), result.Output + result.Error);
            var status = JsonSerializer.Deserialize<SqliteStatus>(await File.ReadAllTextAsync(statusPath))!;
            Assert.NotNull(status.Version);
            Assert.True(status.Library == "system" || !status.Available && !status.Fts5
                && status.Failure!.Contains("without the FTS5", StringComparison.Ordinal), JsonSerializer.Serialize(status));
        }
        finally
        {
            // Delete only the exact directory returned by CreateTempSubdirectory above.
            if (copy.Parent!.FullName != Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
                throw new InvalidOperationException("Unexpected system-SQLite test directory.");
            copy.Delete(recursive: true);
        }
    }

    [Fact]
    public void SystemFallbackChildHost()
    {
        var path = Environment.GetEnvironmentVariable(ChildVariable);
        if (string.IsNullOrEmpty(path)) return;
        File.WriteAllText(path, JsonSerializer.Serialize(SqliteAvailability.Check()));
    }

    private static bool IsBundledNativeLibrary(string name) =>
        name is "e_sqlite3.dll" or "libe_sqlite3.so" or "libe_sqlite3.dylib";
}

using Xunit;
using Xunit.Sdk;

namespace CodeAtlas.Tests;

/// <summary>One real source file stays one set of rows when a symlink also reaches it.</summary>
public sealed partial class SqliteFileUpdateTests
{
    [Fact]
    public async Task SymlinkToAnIncludedFileIsIndexedOnceByItsRealPath()
    {
        // Arrange
        using var fixture = await SampleCopy.CreateAsync();
        var projectFile = Path.Combine(fixture.Root, "Library", "Library.csproj");
        var projectBefore = await File.ReadAllTextAsync(projectFile);
        var nested = Path.Combine(fixture.Root, "Library", "nested");
        Directory.CreateDirectory(nested);
        var onlyOnce = Path.Combine(nested, "OnlyOnce.cs");
        await File.WriteAllTextAsync(onlyOnce, """
            namespace Sample;

            public static class OnlyOnce
            {
                public static int Only() => 2;
            }
            """);
        var via = Path.Combine(fixture.Root, "Library", "via");
        Link(via, nested, directory: true);
        var alias = Path.Combine(fixture.Root, "Library", "Calculator.alias.cs");
        Link(alias, fixture.Calculator, directory: false);
        var sharedDirectory = Path.Combine(fixture.Root, "Shared");
        Directory.CreateDirectory(sharedDirectory);
        var sharedOnly = Path.Combine(sharedDirectory, "LinkedOnly.cs");
        await File.WriteAllTextAsync(sharedOnly, """
            namespace Sample;

            public static class LinkedOnly
            {
                public static int Once() => 4;
            }
            """);
        Link(Path.Combine(fixture.Root, "Library", "LinkedOnly.cs"), sharedOnly, directory: false);
        var outsideRoot = Path.Combine(Path.GetTempPath(), "codeatlas-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        try
        {
            var outside = Path.Combine(outsideRoot, "Outside.cs");
            await File.WriteAllTextAsync(outside, """
                namespace OutsideSample;

                public static class OutsideMarker
                {
                    public static int Nope() => 3;
                }
                """);
            var outsideLink = Path.Combine(fixture.Root, "Library", "OutsideLink.cs");
            Link(outsideLink, outside, directory: false);

            // Act
            var index = await SolutionAnalysis.CreateIndexAsync(fixture.Solution);

            // Assert
            const string onlyOnceFile = "Library/nested/OnlyOnce.cs";
            const string linkedOnlyFile = "Shared/LinkedOnly.cs";
            var library = Assert.Single(index.Projects, project => project.Path == "Library/Library.csproj");
            Assert.Equal(1, library.Files.Count(file => file.EndsWith("Calculator.cs", StringComparison.Ordinal)));
            Assert.Contains(CalculatorFile, library.Files);
            Assert.Contains(onlyOnceFile, library.Files);
            Assert.Contains(linkedOnlyFile, library.Files);
            Assert.DoesNotContain("Library/LinkedOnly.cs", library.Files);
            Assert.DoesNotContain(library.Files, file => file.Contains("alias", StringComparison.Ordinal)
                || file.Contains("/via/", StringComparison.Ordinal) || file.Contains("Outside", StringComparison.Ordinal));
            var calculates = index.Methods.Where(method => method.Name == "Calculate").ToArray();
            Assert.Equal(2, calculates.Length);
            Assert.Equal(calculates.Length, calculates.Select(method => method.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.All(calculates, method => Assert.Equal(CalculatorFile, method.Location.File));
            var only = Assert.Single(index.Methods, method => method.Name == "Only");
            Assert.Equal(onlyOnceFile, only.Location.File);
            var once = Assert.Single(index.Methods, method => method.Name == "Once");
            Assert.Equal(linkedOnlyFile, once.Location.File);
            Assert.DoesNotContain(index.Methods, method => method.Name == "Nope"
                || method.Location.File.Contains("alias", StringComparison.Ordinal)
                || method.Location.File.Contains("/via/", StringComparison.Ordinal)
                || method.Location.File.Contains("Outside", StringComparison.Ordinal));
            Assert.Contains(index.Warnings, warning => warning.Contains("OutsideLink.cs", StringComparison.Ordinal)
                && warning.Contains("outside the solution", StringComparison.Ordinal));
            Assert.Single(index.InputManifest!.Files.Keys, path => path.EndsWith(
                $"{Path.DirectorySeparatorChar}Library{Path.DirectorySeparatorChar}Calculator.cs", StringComparison.Ordinal));
            Assert.DoesNotContain(index.InputManifest.Files.Keys, path => path.Contains("Calculator.alias", StringComparison.Ordinal)
                || path.Contains("OutsideLink", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}via{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}Library{Path.DirectorySeparatorChar}LinkedOnly.cs", StringComparison.Ordinal));
            Assert.Contains(index.InputManifest.Files.Keys, path => path.EndsWith(
                $"{Path.DirectorySeparatorChar}Shared{Path.DirectorySeparatorChar}LinkedOnly.cs", StringComparison.Ordinal));
            Assert.DoesNotContain(index.References.Values.SelectMany(rows => rows), row =>
                row.Location.File.Contains("alias", StringComparison.Ordinal) || row.Location.File.Contains("/via/", StringComparison.Ordinal));

            await IndexStore.PublishAsync(index);
            using var session = await LiveIndexSession.OpenAsync(fixture.Solution);
            var calculatesFound = await session.FindSymbolsAsync("Calculate", "method");
            Assert.Equal(2, calculatesFound.Value.Items.Length);
            Assert.All(calculatesFound.Value.Items, symbol => Assert.Equal(CalculatorFile, symbol.Location.File));
            var onceFound = await session.FindSymbolsAsync("Once", "method");
            Assert.Equal(linkedOnlyFile, Assert.Single(onceFound.Value.Items).Location.File);
            var edited = EditedCalculator(await File.ReadAllTextAsync(fixture.Calculator));
            await session.ApplyAsync(alias, edited);
            var symbols = await session.FindSymbolsAsync("Extra", "method");
            var extra = Assert.Single(symbols.Value.Items);
            Assert.Equal(CalculatorFile, extra.Location.File);
            Assert.Contains("Extra", await File.ReadAllTextAsync(fixture.Calculator), StringComparison.Ordinal);
            Assert.Equal(projectBefore, await File.ReadAllTextAsync(projectFile));
            Assert.True(File.Exists(alias));

            await File.WriteAllTextAsync(alias, EditedAgain(edited));
            var stale = await Assert.ThrowsAsync<StaleIndexException>(() => session.FindSymbolsAsync("Extra", "method"));
            Assert.Contains(CalculatorFile, stale.Diagnostics);
            Assert.DoesNotContain(stale.Diagnostics, file => file.Contains("alias", StringComparison.Ordinal)
                || file.Contains("/via/", StringComparison.Ordinal) || file.Contains("Outside", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(outsideRoot, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// Creates a symlink. Windows needs a privilege for that, so there the test skips and says why.
    /// Elsewhere a failure fails the test, so Ubuntu CI always runs what Windows may skip.
    /// </summary>
    internal static void Link(string path, string target, bool directory)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(path, target);
            else File.CreateSymbolicLink(path, target);
        }
        catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException
            or PlatformNotSupportedException or NotSupportedException)
        {
            throw SkipException.ForSkip("Creating a symlink is not permitted here.");
        }
    }
}

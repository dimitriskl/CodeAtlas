using System.Diagnostics;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class WorkspaceTextTests
{
    [Fact]
    public async Task AppliedBodyEditIsVisibleToTheNextReferenceQuery()
    {
        await using var workspace = await WorkspaceCopy.LoadAsync();
        const string id = "Library/Library.csproj::M:Sample.Calculator.Calculate(System.Int32)";
        var before = await workspace.Analysis.FindReferencesAsync(id);
        Assert.Equal(3, before.Count);

        var source = await File.ReadAllTextAsync(workspace.Calculator);
        var edited = source.Replace(
            "public static int LocalCaller() => Calculate(1);",
            "public static int LocalCaller() => Calculate(1);\n    public static int Extra() => Calculate(4);",
            StringComparison.Ordinal);
        var updated = workspace.Analysis.ApplyDocumentText(workspace.Calculator, edited);

        Assert.Equal(["Library/Calculator.cs"], updated);
        var after = await workspace.Analysis.FindReferencesAsync(id);
        Assert.Equal(4, after.Count);
        Assert.Contains(after, reference => reference.Location.File == "Library/Calculator.cs"
            && reference.Kind == "invocation" && reference.Location.Line == 8);
        Assert.Empty(workspace.Analysis.ApplyDocumentText(Path.Combine(workspace.Root, "Library", "missing.cs"), edited));
        Assert.Equal(4, (await workspace.Analysis.FindReferencesAsync(id)).Count);
    }

    private sealed class WorkspaceCopy : IAsyncDisposable
    {
        internal string Root { get; }
        internal string Calculator => Path.Combine(Root, "Library", "Calculator.cs");
        internal SolutionAnalysis Analysis { get; }

        private WorkspaceCopy(string root, SolutionAnalysis analysis)
        {
            Root = root;
            Analysis = analysis;
        }

        internal static async Task<WorkspaceCopy> LoadAsync()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CodeAtlas.slnx")))
                repository = repository.Parent;
            if (repository is null) throw new InvalidOperationException("Required checkout not found.");
            var root = Path.Combine(repository.FullName, ".codeatlas", "validation", "workspace-text", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            foreach (var relative in new[] { "Sample.slnx", "Library/Library.csproj", "Library/Calculator.cs",
                         "Library/packages.lock.json", "Caller/Caller.csproj", "Caller/Consumer.cs", "Caller/packages.lock.json" })
            {
                var target = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(repository.FullName, "tests", "Fixtures", relative), target);
            }
            foreach (var name in new[] { "Directory.Build.props", "Version.props" })
                File.Copy(Path.Combine(repository.FullName, name), Path.Combine(root, name));
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "restore", "Sample.slnx", "--locked-mode" }) start.ArgumentList.Add(argument);
            var process = await ChildProcess.RunAsync(start);
            if (process.ExitCode != 0) throw new InvalidOperationException(process.Output + process.Error);
            var analysis = await SolutionAnalysis.LoadAsync(Path.Combine(root, "Sample.slnx"));
            return new WorkspaceCopy(root, analysis);
        }

        public async ValueTask DisposeAsync()
        {
            Analysis.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
            await Task.CompletedTask;
        }
    }
}

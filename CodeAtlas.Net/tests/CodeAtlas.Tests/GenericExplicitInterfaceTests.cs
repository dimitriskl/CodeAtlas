using System.Diagnostics;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class GenericExplicitInterfaceTests
{
    private const string Source = """
        namespace Sample;
        public interface ILogger<TCategory> { void BeginGroup(string name); }
        public sealed class Logger<TCategory> : ILogger<TCategory>
        {
            void ILogger<TCategory>.BeginGroup(string name) { }
        }
        public static class Caller
        {
            public static void Run(ILogger<int> logger) => logger.BeginGroup("start");
        }
        """;

    [Fact]
    public async Task ExplicitImplementationOfGenericInterfaceInGenericTypeIsIndexedWithReferences()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("codeatlas-generic-explicit-");
        try
        {
            var solution = Path.Combine(directory.FullName, "Generic.slnx");
            await File.WriteAllTextAsync(solution, "<Solution><Project Path=\"Generic.csproj\" /></Solution>");
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "Generic.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "Logger.cs"), Source);
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory.FullName, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("restore");
            start.ArgumentList.Add(solution);
            var process = await ChildProcess.RunAsync(start);
            Assert.True(process.ExitCode == 0, process.Output + process.Error);
            const string explicitId = "Generic.csproj::M:Sample.Logger`1.Sample#ILogger{TCategory}#BeginGroup(System.String)";

            // Act
            var index = await SolutionAnalysis.CreateIndexAsync(solution);

            // Assert
            Assert.Contains(index.Methods, method => method.Id == explicitId);
            var reference = Assert.Single(index.References[explicitId]);
            Assert.Equal("Logger.cs", reference.Location.File);
            Assert.Equal(9, reference.Location.Line);
            Assert.Equal("invocation", reference.Kind);
        }
        finally { directory.Delete(recursive: true); }
    }
}

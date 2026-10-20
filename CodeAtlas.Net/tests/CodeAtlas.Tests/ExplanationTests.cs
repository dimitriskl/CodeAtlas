using Xunit;

namespace CodeAtlas.Tests;

public sealed class ExplanationTests
{
    [Fact]
    public async Task ImportRejectsAChangedSymbolAndReadMarksAReplacedFileStale()
    {
        // Arrange
        var symbol = Sample("Original wording.");
        var changed = symbol with { Documentation = Documentation("Changed wording.") };
        var root = Path.Combine(Path.GetTempPath(), "codeatlas-explain-" + Guid.NewGuid().ToString("N"));
        var request = Explanations.CreateRequest(symbol, "index", "0.00.16");
        var response = new ExplanationResponse(symbol.Id, "index", request.ContentHash, "Generated note.", "local-agent");

        // Act
        var stored = await Explanations.ImportAsync(root, symbol, "index", response);
        var current = await Explanations.ReadAsync(root, symbol, "index");
        var stale = await Explanations.ReadAsync(root, changed, "index");
        var mismatch = await Record.ExceptionAsync(() => Explanations.ImportAsync(root, changed, "index", response));

        // Assert
        Assert.Equal("current", stored.Status);
        Assert.Equal("current", current!.Status);
        Assert.Equal("Generated note.", current.Text);
        Assert.Equal("stale", stale!.Status);
        Assert.Equal("explanation_stale", Assert.IsType<QueryException>(mismatch).Code);
        Directory.Delete(root, recursive: true);
    }

    private static DocumentedSymbol Sample(string text)
    {
        var position = new SourcePosition("Source.cs", 1, 1);
        return new("Library.csproj::M:Sample.Work.Run", "Run", "method", null, "void Run()",
            "Library.csproj", "Sample", "Sample.Work", position, [position], Documentation(text));
    }

    private static DeveloperDocumentation Documentation(string text)
    {
        var range = new DocumentationRange(new("Source.cs", 1, 1), new("Source.cs", 1, 20));
        var evidence = new DocumentationEvidence("Library.csproj::M:Sample.Work.Run", "comment", range, []);
        return new("present", [], [new("summary", null, null, text, text, evidence)], [], []);
    }
}

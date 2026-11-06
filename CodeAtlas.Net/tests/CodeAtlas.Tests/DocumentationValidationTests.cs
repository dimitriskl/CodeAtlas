using System.Text.Json.Nodes;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class DocumentationValidationTests(IndexedSolution fixture) : IClassFixture<IndexedSolution>
{
    [Theory]
    [InlineData("absent-catalog")]
    [InlineData("absent-coverage")]
    [InlineData("coverage-version")]
    [InlineData("duplicate-id")]
    [InlineData("null-documentation")]
    [InlineData("null-comments")]
    [InlineData("invalid-status")]
    [InlineData("wrong-position")]
    [InlineData("missing-method")]
    public async Task SchemaTwoRejectsMissingAndMalformedCatalogBeforeQueries(string corruption)
    {
        // Arrange
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        var json = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(fixture.Index, options))!.AsObject();
        switch (corruption)
        {
            case "absent-catalog": json.Remove("symbols"); break;
            case "absent-coverage": json.Remove("documentationCoverage"); break;
            case "coverage-version": json["documentationCoverage"]!["version"] = 999; break;
            case "duplicate-id": json["symbols"]![1]!["id"] = json["symbols"]![0]!["id"]!.GetValue<string>(); break;
            case "null-documentation": json["symbols"]![0]!["documentation"] = null; break;
            case "null-comments": json["symbols"]![0]!["documentation"]!["comments"] = null; break;
            case "invalid-status": json["symbols"]![0]!["documentation"]!["status"] = "complete-runtime-proof"; break;
            case "wrong-position": json["symbols"]![0]!["locations"]![0]!["line"] = 0; break;
            case "missing-method":
                var methods = json["symbols"]!.AsArray();
                methods.Remove(methods.First(symbol => symbol!["kind"]!.GetValue<string>() == "method"));
                break;
        }
        var path = Path.Combine(fixture.Root, ".codeatlas", "malformed-documentation-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        try
        {
            // Act
            var failure = await Record.ExceptionAsync(() => IndexStore.LoadAsync(path));

            // Assert
            Assert.Equal("invalid_index", Assert.IsType<IndexFormatException>(failure).Code);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SchemaOneRemainsOrdinaryQueryCompatibleButCannotSupplyInventedDocumentation()
    {
        // Arrange
        var legacy = fixture.Index with { SchemaVersion = 1, Symbols = null, DocumentationCoverage = null };
        var snapshot = new LoadedSnapshot(legacy, "legacy-index.json", null, false);
        var expected = fixture.Index.Methods.First();

        // Act
        var found = QueryPaging.Methods(IndexStore.FindMethods(legacy, expected.Name), new(), legacy.IndexId);
        var references = IndexStore.FindReferences(legacy, expected.Id);
        var failure = await Record.ExceptionAsync(() => DocumentationSnapshotQueries.GetAsync(snapshot, expected.Id));

        // Assert
        Assert.Contains(expected, found.Items);
        Assert.Equal(fixture.Index.References[expected.Id], references);
        Assert.Equal("rebuild_required", Assert.IsType<QueryException>(failure).Code);
    }

    [Fact]
    public async Task CompactPreviewBoundsTextWithoutChangingAuthoredFullDocumentation()
    {
        // Arrange
        var authored = new string('x', SymbolSummary.MaximumSummaryLength + 100);
        using var virtualSource = await DocumentationFixture.CreateAsync(("Library", "Preview.cs",
            "namespace Docs; public class Preview {\n/// <summary>" + authored + "</summary>\npublic void Run() {} }"));
        var symbol = (await virtualSource.ExtractAsync()).Single(candidate => candidate.Name == "Run");

        // Act
        var preview = SymbolSummary.From(symbol);

        // Assert
        Assert.Equal(authored[..SymbolSummary.MaximumSummaryLength], preview.Documentation.Summary);
        Assert.True(preview.Documentation.SummaryTruncated);
        Assert.Equal("developer-xml", preview.Documentation.Origin);
        Assert.Equal(authored, symbol.Documentation.Sections.Single().Text);
        Assert.Contains(authored, symbol.Documentation.Comments.Single().RawComment);
    }
}

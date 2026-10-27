using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class SplitCatalogStorageTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("codeatlas-split-storage-");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private async Task<CodebaseIndex> CreateAsync()
    {
        var solution = Path.Combine(root.FullName, "Empty.slnx");
        await File.WriteAllTextAsync(solution, "<Solution />");
        return new CodebaseIndex
        {
            SchemaVersion = IndexStore.SchemaVersion, IndexId = Guid.NewGuid().ToString("N"), Solution = solution,
            CreatedAtUtc = DateTimeOffset.UtcNow, Warnings = [], Projects = [], Types = [], Methods = [], References = [],
            InputRoots = [root.FullName], ExplicitInputs = [solution],
            Fingerprints = new() { [solution] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(solution))) },
            Symbols = [], DocumentationCoverage = DocumentationCoverage.Current,
            StructuralCatalog = new(StructuralCoverage.Current, [], [])
        };
    }

    [Theory]
    [InlineData("documentation")]
    [InlineData("structural")]
    public async Task FullLoadsAndPublicationRejectMissingMandatoryCatalogs(string missing)
    {
        // Arrange
        var valid = await CreateAsync();
        var invalid = missing == "documentation" ? valid with { Symbols = null } : valid with { StructuralCatalog = null };
        var path = Path.Combine(root.FullName, "raw.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(invalid, JsonOptions));

        // Act
        var readFailure = await Record.ExceptionAsync(() => IndexStore.LoadAsync(path));
        var publishFailure = await Record.ExceptionAsync(() => IndexStore.PublishAsync(invalid));

        // Assert
        Assert.Equal("invalid_index", Assert.IsType<IndexFormatException>(readFailure).Code);
        Assert.Equal("invalid_index", Assert.IsType<IndexFormatException>(publishFailure).Code);
        Assert.False(File.Exists(IndexStore.DefaultPath(valid.Solution)));
    }

    [Fact]
    public async Task ImmutableIndexRetainsItsOwnVerifiedCatalogAfterPointerMoves()
    {
        // Arrange
        var index = await CreateAsync();
        var first = await IndexStore.PublishAsync(index);
        var next = index with { IndexId = Guid.NewGuid().ToString("N") };
        await IndexStore.PublishAsync(next);

        // Act
        var retained = await IndexStore.LoadSnapshotAsync(first.IndexPath);

        // Assert
        Assert.Equal(first.Index.IndexId, retained.Index.IndexId);
        Assert.True(retained.VerifiedGeneration);
        Assert.Equal(first.GenerationDirectory, retained.GenerationDirectory);
        Assert.NotNull(retained.Index.Symbols);
        Assert.NotNull(retained.Index.StructuralCatalog);
    }

    [Fact]
    public async Task CompleteRetainedGenerationCanBeCopiedToAnArbitraryDirectory()
    {
        // Arrange
        var original = await IndexStore.PublishAsync(await CreateAsync());
        var copy = Directory.CreateDirectory(Path.Combine(root.FullName, "retained-before"));
        foreach (var path in Directory.GetFiles(original.GenerationDirectory!))
            File.Copy(path, Path.Combine(copy.FullName, Path.GetFileName(path)));

        // Act
        var loaded = await IndexStore.LoadSnapshotAsync(Path.Combine(copy.FullName, "index.json"));

        // Assert
        Assert.True(loaded.VerifiedGeneration);
        Assert.Equal(original.Index.IndexId, loaded.Index.IndexId);
        Assert.Equal(copy.FullName, loaded.GenerationDirectory);
        Assert.NotNull(loaded.Index.StructuralCatalog);
    }

    [Fact]
    public async Task OrdinaryMethodLoadSkipsCatalogDeserializationButFullLoadRejectsCorruption()
    {
        // Arrange
        var published = await LegacyJsonPublication.PublishAsync(await CreateAsync());
        var catalog = Path.Combine(published.GenerationDirectory!, "catalog.json");
        var bytes = await File.ReadAllBytesAsync(catalog);
        bytes[0] = (byte)'!';
        await File.WriteAllBytesAsync(catalog, bytes);

        // Act
        var lightweight = await IndexStore.LoadSnapshotAsync(published.Path, includeCatalog: false);
        var fullFailure = await Record.ExceptionAsync(() => IndexStore.LoadSnapshotAsync(published.Path));

        // Assert
        Assert.Null(lightweight.Index.Symbols);
        Assert.Null(lightweight.Index.StructuralCatalog);
        Assert.Equal("invalid_index", Assert.IsType<IndexFormatException>(fullFailure).Code);
    }

    [Fact]
    public async Task CatalogIdentityMustAgreeEvenWhenManifestDigestMatches()
    {
        // Arrange
        var published = await LegacyJsonPublication.PublishAsync(await CreateAsync());
        var catalogPath = Path.Combine(published.GenerationDirectory!, "catalog.json");
        var catalog = JsonNode.Parse(await File.ReadAllTextAsync(catalogPath))!;
        catalog["indexId"] = Guid.NewGuid().ToString("N");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(catalog);
        await File.WriteAllBytesAsync(catalogPath, bytes);
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(published.Path))!;
        manifest["artifacts"]!["catalog.json"]!["length"] = bytes.Length;
        manifest["artifacts"]!["catalog.json"]!["sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        await File.WriteAllTextAsync(published.Path, manifest.ToJsonString());

        // Act
        var failure = await Record.ExceptionAsync(() => IndexStore.LoadSnapshotAsync(published.Path));

        // Assert
        Assert.Equal("invalid_index", Assert.IsType<IndexFormatException>(failure).Code);
    }

    [Theory]
    [InlineData("indexId")]
    [InlineData("solution")]
    [InlineData("both-null")]
    public async Task PartialOrNullCatalogIdentityHeadersAreRejected(string missing)
    {
        // Arrange
        var published = await LegacyJsonPublication.PublishAsync(await CreateAsync());
        var catalogPath = Path.Combine(published.GenerationDirectory!, "catalog.json");
        var catalog = JsonNode.Parse(await File.ReadAllTextAsync(catalogPath))!.AsObject();
        if (missing == "both-null") { catalog["indexId"] = null; catalog["solution"] = null; }
        else catalog.Remove(missing);
        await ReplaceCatalogAsync(published, catalog);

        // Act
        var failure = await Record.ExceptionAsync(() => IndexStore.LoadSnapshotAsync(published.Path));

        // Assert
        Assert.Equal("invalid_index", Assert.IsType<IndexFormatException>(failure).Code);
    }

    [Fact]
    public async Task LegacyManifestCatalogRemainsVerifiedButRawSplitInspectionIsExplicitlyUnverified()
    {
        // Arrange
        var published = await LegacyJsonPublication.PublishAsync(await CreateAsync());
        var catalogPath = Path.Combine(published.GenerationDirectory!, "catalog.json");
        var catalog = JsonNode.Parse(await File.ReadAllTextAsync(catalogPath))!.AsObject();
        catalog.Remove("indexId");
        catalog.Remove("solution");
        await ReplaceCatalogAsync(published, catalog);
        File.Delete(Path.Combine(published.GenerationDirectory!, "generation.json"));

        // Act
        var verified = await IndexStore.LoadSnapshotAsync(published.Path);
        var implicitRawFailure = await Record.ExceptionAsync(() => IndexStore.LoadSnapshotAsync(published.IndexPath));
        var explicitRaw = await IndexStore.LoadUnverifiedSnapshotAsync(published.IndexPath);

        // Assert
        Assert.True(verified.VerifiedGeneration);
        Assert.NotNull(verified.Index.StructuralCatalog);
        Assert.Equal("invalid_index", Assert.IsType<IndexFormatException>(implicitRawFailure).Code);
        Assert.False(explicitRaw.VerifiedGeneration);
        Assert.Null(explicitRaw.GenerationDirectory);
        Assert.NotNull(explicitRaw.Index.StructuralCatalog);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InlineLegacySchemasKeepTheirStrictUnverifiedReadContract(int schema)
    {
        // Arrange
        var current = await CreateAsync();
        var legacy = current with { SchemaVersion = schema, StructuralCatalog = null,
            Symbols = schema == 1 ? null : [], DocumentationCoverage = schema == 1 ? null : DocumentationCoverage.Current };
        var path = Path.Combine(root.FullName, "legacy.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(legacy, JsonOptions));

        // Act
        var loaded = await IndexStore.LoadSnapshotAsync(path);

        // Assert
        Assert.False(loaded.VerifiedGeneration);
        Assert.Equal(schema, loaded.Index.SchemaVersion);
    }

    private static async Task ReplaceCatalogAsync(LoadedSnapshot published, JsonObject catalog)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(catalog);
        await File.WriteAllBytesAsync(Path.Combine(published.GenerationDirectory!, "catalog.json"), bytes);
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(published.Path))!;
        manifest["artifacts"]!["catalog.json"]!["length"] = bytes.Length;
        manifest["artifacts"]!["catalog.json"]!["sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        await File.WriteAllTextAsync(published.Path, manifest.ToJsonString());
    }

    public void Dispose() => root.Delete(recursive: true);
}

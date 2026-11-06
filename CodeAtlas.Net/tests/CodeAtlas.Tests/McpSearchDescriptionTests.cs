using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAtlas.Cli;
using CodeAtlas.Explanations;
using Xunit;
using static CodeAtlas.Tests.McpLines;

namespace CodeAtlas.Tests;

/// <summary><c>search_symbols</c> over saved AI descriptions, imported by hand so no model runs.</summary>
// Task #04: MCP tool search_symbols
public sealed class McpSearchDescriptionTests(ExplanationCliContractFixture fixture) : IClassFixture<ExplanationCliContractFixture>
{
    private const string DescriptionWord = "quartzharbor";
    private const string Description = DescriptionWord + " source transformation";

    private string Workspace => fixture.Root;

    [Fact]
    public async Task AssistedReadsSavedDescriptionsWithNoCredentialsAndCallsNoProvider()
    {
        // Arrange
        var (index, method, _) = await PublishAsync();
        await ImportAsync(method, Description);
        using var provider = new TcpListener(IPAddress.Loopback, 0);
        provider.Start();
        var saved = StoredFiles();
        var start = ServerWithoutCredentials((IPEndPoint)provider.LocalEndpoint);
        var input = Search(1, DescriptionWord) + "\n" + Search(2, DescriptionWord, mode: "names") + "\n";

        // Act
        var server = await ChildProcess.RunAsync(start, input: input);

        // Assert
        Assert.True(server.ExitCode == 0, server.Error);
        var responses = server.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        var assisted = Envelope(responses[0], isError: false);
        var row = Assert.Single(assisted["result"]!["symbols"]!.AsArray())!;
        Assert.Equal(method.Id, row["symbol"]!["id"]!.GetValue<string>());
        var reason = row["reasons"]!.AsArray().Single(item => item!["field"]!.GetValue<string>() == "ai-description")!;
        Assert.Equal(Description, reason["aiDescriptionEvidence"]!["claim"]!["text"]!.GetValue<string>());
        Assert.StartsWith(index.IndexId + ":ai:", assisted["pagination"]!["snapshotId"]!.GetValue<string>());
        Assert.Empty(Envelope(responses[1], isError: false)["result"]!["symbols"]!.AsArray());
        Assert.False(provider.Pending());
        var after = StoredFiles();
        Assert.Equal(saved.Keys.Order(), after.Keys.Order());
        foreach (var path in saved.Keys) Assert.Equal(saved[path], after[path]);
    }

    [Fact]
    public async Task AssistedPagesContinueWithTheSnapshotIdTheyReturned()
    {
        // Arrange
        var (index, method, other) = await PublishAsync();
        await ImportAsync(method, Description);
        await ImportAsync(other, Description);
        var first = Envelope((await Talk(fixture.Solution, [Search(1, DescriptionWord, limit: 1)]))[0], isError: false);
        var token = first["pagination"]!["snapshotId"]!.GetValue<string>();

        // Act
        var responses = await Talk(fixture.Solution, [Search(1, DescriptionWord, offset: 1, limit: 1, snapshotId: token)]);

        // Assert
        Assert.StartsWith(index.IndexId + ":ai:", token);
        Assert.Equal(2, first["pagination"]!["total"]!.GetValue<int>());
        var second = Envelope(responses[0], isError: false);
        Assert.Equal(token, second["pagination"]!["snapshotId"]!.GetValue<string>());
        Assert.NotEqual(first["result"]!["symbols"]![0]!["symbol"]!["id"]!.GetValue<string>(),
            second["result"]!["symbols"]![0]!["symbol"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task AChangedSavedDescriptionRefusesAContinuationOfTheOldView()
    {
        // Arrange
        var (_, method, _) = await PublishAsync();
        await ImportAsync(method, Description);
        var first = Envelope((await Talk(fixture.Solution, [Search(1, DescriptionWord)]))[0], isError: false);
        var token = first["pagination"]!["snapshotId"]!.GetValue<string>();
        await ImportAsync(method, DescriptionWord + " revised transformation");

        // Act
        var responses = await Talk(fixture.Solution, [Search(1, DescriptionWord, snapshotId: token)]);

        // Assert
        var mismatch = Envelope(responses[0], isError: true);
        Assert.Equal("search_symbols", mismatch["command"]!.GetValue<string>());
        Assert.Equal("snapshot_mismatch", mismatch["error"]!["code"]!.GetValue<string>());
        Assert.Equal("The index was replaced. Start a new query at offset zero.", mismatch["error"]!["message"]!.GetValue<string>());
        Assert.Null(mismatch["freshness"]);
        Assert.Null(mismatch["coverage"]);
    }

    [Fact]
    public async Task OneServerRefusesAPageFromTheOtherModeOrAChangedDescription()
    {
        // Arrange
        var (_, method, other) = await PublishAsync();
        await ImportAsync(method, Description);
        await ImportAsync(other, Description);
        var host = McpHost.Start(fixture.Solution);
        var names = await SnapshotIdAsync(host, Search(1, DescriptionWord, mode: "names", limit: 1));
        var oldView = await SnapshotIdAsync(host, Search(2, DescriptionWord, limit: 1));
        await ImportAsync(method, DescriptionWord + " revised transformation");
        var view = await SnapshotIdAsync(host, Search(3, DescriptionWord, limit: 1));
        var lines = new[]
        {
            Search(4, DescriptionWord, snapshotId: names),
            Search(5, DescriptionWord, mode: "names", snapshotId: view),
            Search(6, DescriptionWord, snapshotId: oldView),
            Search(7, DescriptionWord, offset: 1, limit: 1, snapshotId: view),
            Search(8, DescriptionWord, mode: "names", snapshotId: names)
        };

        // Act
        var responses = await AskAsync(host, lines);

        // Assert
        Assert.NotEqual(oldView, view);
        foreach (var refused in responses[..3])
            Assert.Equal("snapshot_mismatch", Envelope(refused, isError: true)["error"]!["code"]!.GetValue<string>());
        Assert.Equal(view, Envelope(responses[3], isError: false)["pagination"]!["snapshotId"]!.GetValue<string>());
        Assert.Equal(names, Envelope(responses[4], isError: false)["pagination"]!["snapshotId"]!.GetValue<string>());
    }

    private static async Task<JsonObject[]> AskAsync(McpHost host, params string[] lines)
    {
        var responses = new List<JsonObject>();
        foreach (var line in lines) responses.Add((await host.HandleLineAsync(line))!);
        return [.. responses];
    }

    private static async Task<string> SnapshotIdAsync(McpHost host, string line) =>
        Envelope((await AskAsync(host, line))[0], isError: false)["pagination"]!["snapshotId"]!.GetValue<string>();

    private async Task<(CodebaseIndex Index, DocumentedSymbol Method, DocumentedSymbol Other)> PublishAsync()
    {
        var index = await SolutionAnalysis.CreateIndexAsync(fixture.Solution);
        await IndexStore.SaveAsync(index);
        var methods = index.Symbols!.Where(symbol => symbol.Kind == "method").ToArray();
        return (index, methods.Single(symbol => symbol.Name == "Double"), methods.First(symbol => symbol.Name.EndsWith("Transform", StringComparison.Ordinal)));
    }

    // The evidence holds "value * 2" for both selected methods. A claim must quote its evidence.
    private async Task ImportAsync(DocumentedSymbol symbol, string description)
    {
        const string quote = "value * 2";
        var service = ExplanationSnapshotAdapter.ForSnapshot(await IndexStore.LoadSnapshotAsync(fixture.IndexPath));
        var request = await service.ExportAsync(symbol.Id, Path.Combine(fixture.ExchangeDirectory, Guid.NewGuid().ToString("N") + ".json"));
        var source = request.Evidence.First(item => item.Role == "selected-declaration");
        var response = new ExplanationResponse(1, request.RequestId, request.IndexId, symbol.Id, request.ContextFingerprint,
            new("manual-import-fixture", "no-model-executed", "fixture/v1", DateTimeOffset.UtcNow),
            [new(description, "interpretation", [new(source.Id, source.Text.IndexOf(quote, StringComparison.Ordinal), quote.Length, quote)])], []);
        var path = Path.Combine(fixture.ExchangeDirectory, "response-" + request.RequestId + ".json");
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(response, ExplanationContract.Json));
        await service.AcceptAsync(path);
    }

    private string Search(int id, string query, string? mode = null, int? offset = null, int? limit = null, string? snapshotId = null)
    {
        var arguments = new JsonObject { ["workspace"] = Workspace, ["query"] = query, ["kind"] = "all" };
        if (mode is not null) arguments["mode"] = mode;
        if (offset is not null) arguments["offset"] = offset;
        if (limit is not null) arguments["limit"] = limit;
        if (snapshotId is not null) arguments["snapshotId"] = snapshotId;
        return Tool(id, "search_symbols", arguments);
    }

    // A provider endpoint and model are set, with no key anywhere, so any provider call would reach the listener.
    private ProcessStartInfo ServerWithoutCredentials(IPEndPoint provider)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(Repository(), "src", "CodeAtlas.Cli", "bin", configuration, "net10.0", "codeatlas.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, StandardInputEncoding = new UTF8Encoding(false), CreateNoWindow = true };
        foreach (var name in start.Environment.Keys.Where(CarriesCredentials).ToArray()) start.Environment.Remove(name);
        start.Environment["CODEATLAS_AI_MODEL"] = "fixture-model";
        start.Environment["CODEATLAS_AI_ENDPOINT"] = $"http://{provider}/v1/chat/completions";
        foreach (var argument in new[] { cli, "mcp", "--solution", fixture.Solution }) start.ArgumentList.Add(argument);
        return start;
    }

    private static bool CarriesCredentials(string name) =>
        name.StartsWith("CODEATLAS_AI_", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase);

    private Dictionary<string, byte[]> StoredFiles() =>
        Directory.GetFiles(Path.Combine(Workspace, ".codeatlas"), "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
}

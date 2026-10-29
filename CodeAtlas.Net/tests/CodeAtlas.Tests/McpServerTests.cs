using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeAtlas.Cli;
using Xunit;
using static CodeAtlas.Tests.McpLines;

namespace CodeAtlas.Tests;

public sealed class McpServerTests(IndexedSolution fixture) : IClassFixture<IndexedSolution>
{
    [Fact]
    public async Task FindSymbolMatchesCliPagesAndRefusesStaleWithoutStopping()
    {
        // Arrange
        var warned = SnapshotTestIdentity.NewGeneration(fixture.Index) with { Warnings = [.. fixture.Index.Warnings, "Recorded index warning"] };
        await IndexStore.SaveAsync(warned);
        var solution = Path.Combine(fixture.Root, "Sample.slnx");
        var calculator = Path.Combine(fixture.Root, "Library", "Calculator.cs");
        var original = await File.ReadAllTextAsync(calculator);
        var cli = await RunCli("find-symbol", "--index", fixture.IndexPath, "--name", "Calculate", "--kind", "method");
        var firstPage = await RunCli("find-symbol", "--index", fixture.IndexPath, "--name", "Calculate", "--kind", "method", "--limit", "1");
        var secondPage = await RunCli("find-symbol", "--index", fixture.IndexPath, "--name", "Calculate", "--kind", "method",
            "--limit", "1", "--offset", "1", "--snapshot", warned.IndexId);
        var published = await IndexStore.OpenSnapshotAsync(fixture.IndexPath);
        var lines = new[]
        {
            Request(1, "initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "0" } }),
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            Request(2, "tools/list"),
            Call(3, fixture.Root, "Calculate", kind: "method"),
            Call(4, fixture.Root, "Calculate", kind: "method", limit: 1),
            Call(5, fixture.Root, "Calculate", kind: "method", offset: 1, snapshotId: warned.IndexId),
            Call(6, fixture.Root, "Calculate", kind: "method", project: "missing.csproj"),
            Call(7, fixture.Root, "Calculate", kind: "method", snapshotId: "previous-snapshot"),
            Call(8, fixture.Root, "Calculate", kind: "method", offset: 1),
            Call(9, fixture.Root, "Calculate", kind: "method"),
            Request(10, "tools/list")
        };
        try
        {
            // Act
            var responses = await Talk(solution, lines, beforeLine: index =>
            {
                if (index == 9) File.AppendAllText(calculator, Environment.NewLine + "// changed");
            });

            // Assert
            Assert.Equal(0, cli.Exit);
            Assert.Equal(10, responses.Length);
            var initialized = responses[0]["result"]!;
            Assert.Equal("2025-06-18", initialized["protocolVersion"]!.GetValue<string>());
            Assert.False(initialized["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>());
            Assert.Equal("codeatlas", initialized["serverInfo"]!["name"]!.GetValue<string>());
            Assert.Equal(CodeAtlasVersion.Current, initialized["serverInfo"]!["version"]!.GetValue<string>());
            var tool = responses[1]["result"]!["tools"]![0]!;
            Assert.Equal("find_symbol", tool["name"]!.GetValue<string>());
            Assert.Equal(new[] { "workspace", "name" }, tool["inputSchema"]!["required"]!.AsArray().Select(item => item!.GetValue<string>()));
            Assert.Equal(new[] { "workspace", "name", "kind", "project", "offset", "limit", "snapshotId" },
                tool["inputSchema"]!["properties"]!.AsObject().Select(property => property.Key));

            var found = Envelope(responses[2], isError: false);
            AssertShape(found, success: true);
            Assert.Equal(published.Index.Warnings.Length, found["warningCount"]!.GetValue<int>());
            Assert.Equal(cli.Json["warningCount"]!.GetValue<int>(), found["warningCount"]!.GetValue<int>());
            Assert.True(found["warningCount"]!.GetValue<int>() >= 1);
            Assert.Equal("C#", found["coverage"]!["language"]!.GetValue<string>());
            Assert.False(found["coverage"]!["runtimeCallGraph"]!.GetValue<bool>());
            Assert.Equal("unknown", found["coverage"]!["templateCoverage"]!.GetValue<string>());
            Assert.Equal(cli.Json["freshness"]!["state"]!.GetValue<string>(), found["freshness"]!["state"]!.GetValue<string>());
            Assert.False(found["freshness"]!["isStale"]!.GetValue<bool>());
            Assert.Equal("tracked-input-content-hashes", found["freshness"]!["scope"]!.GetValue<string>());
            Assert.Matches(Stamp, found["freshness"]!["checkedAtUtc"]!.GetValue<string>());
            Assert.Equal(published.Index.IndexId, found["pagination"]!["snapshotId"]!.GetValue<string>());
            Assert.True(found["pagination"]!["continuationSupported"]!.GetValue<bool>());
            AssertSymbols(cli.Json["symbols"]!, found["result"]!["symbols"]!);

            AssertSymbols(firstPage.Json["symbols"]!, Envelope(responses[3], false)["result"]!["symbols"]!);
            var continued = Envelope(responses[4], false);
            AssertSymbols(secondPage.Json["symbols"]!, continued["result"]!["symbols"]!);
            Assert.Equal(warned.IndexId, continued["pagination"]!["snapshotId"]!.GetValue<string>());
            Assert.True(continued["pagination"]!["continuationSupported"]!.GetValue<bool>());

            var unknownProject = Envelope(responses[5], isError: false);
            Assert.Empty(unknownProject["result"]!["symbols"]!.AsArray());
            Assert.Equal(0, unknownProject["pagination"]!["total"]!.GetValue<int>());
            Assert.True(unknownProject["pagination"]!["continuationSupported"]!.GetValue<bool>());

            var mismatch = Envelope(responses[6], isError: true);
            Assert.Equal("snapshot_mismatch", mismatch["error"]!["code"]!.GetValue<string>());
            Assert.Equal("The index was replaced. Start a new query at offset zero.", mismatch["error"]!["message"]!.GetValue<string>());
            Assert.Null(mismatch["freshness"]);
            Assert.Null(mismatch["coverage"]);
            Assert.Equal(0, mismatch["warningCount"]!.GetValue<int>());
            Assert.False(mismatch.ContainsKey("pagination"));

            var offset = Envelope(responses[7], isError: true);
            Assert.Equal("invalid_arguments", offset["error"]!["code"]!.GetValue<string>());
            Assert.Equal("offset greater than zero requires snapshotId.", offset["error"]!["message"]!.GetValue<string>());

            var stale = Envelope(responses[8], isError: true);
            AssertShape(stale, success: false);
            Assert.Equal("stale_index", stale["error"]!["code"]!.GetValue<string>());
            Assert.Equal("The saved index is stale.", stale["error"]!["message"]!.GetValue<string>());
            Assert.Empty(stale["error"]!["diagnostics"]!.AsArray());
            Assert.True(stale["freshness"]!["isStale"]!.GetValue<bool>());
            Assert.Contains("Library/Calculator.cs", stale["freshness"]!["changedFiles"]!.AsArray().Select(file => file!.GetValue<string>()));
            Assert.NotNull(stale["coverage"]);
            Assert.Equal(published.Index.Warnings.Length, stale["warningCount"]!.GetValue<int>());
            Assert.False(stale.ContainsKey("result"));
            Assert.False(stale.ContainsKey("pagination"));
            Assert.Equal("find_symbol", responses[9]["result"]!["tools"]![0]!["name"]!.GetValue<string>());
        }
        finally
        {
            await File.WriteAllTextAsync(calculator, original);
            await fixture.RestorePublicationAsync();
        }
    }

    private static void AssertSymbols(JsonNode expected, JsonNode actual)
    {
        var left = expected.AsArray();
        var right = actual.AsArray();
        Assert.Equal(left.Count, right.Count);
        for (var i = 0; i < left.Count; i++)
        {
            var cli = left[i]!;
            var symbol = right[i]!.AsObject();
            Assert.Equal(new[] { "id", "kind", "qualifiedName", "project", "path", "line", "column", "signatures" }, symbol.Select(property => property.Key));
            Assert.Equal(cli["id"]!.GetValue<string>(), symbol["id"]!.GetValue<string>());
            Assert.Equal("method", symbol["kind"]!.GetValue<string>());
            var containing = cli["containingType"] is null ? null : cli["containingType"]!.GetValue<string>();
            var qualified = string.IsNullOrEmpty(containing) ? cli["name"]!.GetValue<string>() : containing + "." + cli["name"]!.GetValue<string>();
            Assert.Equal(qualified, symbol["qualifiedName"]!.GetValue<string>());
            Assert.Equal(cli["project"]!.GetValue<string>(), symbol["project"]!.GetValue<string>());
            Assert.Equal(cli["location"]!["file"]!.GetValue<string>(), symbol["path"]!.GetValue<string>());
            Assert.Equal(cli["location"]!["line"]!.GetValue<int>(), symbol["line"]!.GetValue<int>());
            Assert.Equal(cli["location"]!["column"]!.GetValue<int>(), symbol["column"]!.GetValue<int>());
            Assert.Equal(new[] { cli["signature"]!.GetValue<string>() }, symbol["signatures"]!.AsArray().Select(item => item!.GetValue<string>()));
        }
    }
}

public sealed class McpIndexFailureTests
{
    [Fact]
    public async Task MissingUnknownAndInvalidIndexesKeepTheServerReading()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("codeatlas-mcp-").FullName;
        var repository = Repository();
        var solution = Path.Combine(root, "Sample.slnx");
        File.Copy(Path.Combine(repository, "tests", "Fixtures", "Sample.slnx"), solution);
        var manifest = IndexStore.DefaultPath(solution);
        var previous = Directory.GetCurrentDirectory();
        var lines = new[]
        {
            Request(1, "tools/call", new JsonObject { ["name"] = "find_symbol", ["arguments"] = Arguments(".", "Calculate") }),
            Request(2, "tools/call", new JsonObject { ["name"] = "find_symbol", ["arguments"] = Arguments("Sample.slnx", "Calculate", snapshotId: "previous-snapshot") }),
            Request(3, "tools/call", new JsonObject { ["name"] = "find_symbol", ["arguments"] = Arguments(".", "Calculate") }),
            Request(4, "tools/list"),
            Request(5, "tools/call", new JsonObject { ["name"] = "find_symbol", ["arguments"] = Arguments(".", "Calculate") }),
            Request(6, "tools/list"),
            Request(7, "tools/call", new JsonObject { ["name"] = "find_symbol", ["arguments"] = Arguments(".", "") }),
            Request(8, "tools/call", new JsonObject { ["name"] = "find_symbol", ["arguments"] = Arguments(".", "Calculate", kind: "") }),
            Request(9, "tools/call", new JsonObject { ["name"] = "find_symbol", ["arguments"] = Arguments(".", "Calculate", kind: "component") }),
            Request(10, "tools/call", new JsonObject { ["name"] = "find_symbol", ["arguments"] = Arguments(".", "Calculate", project: "") }),
            Request(11, "tools/call", new JsonObject { ["name"] = "find_symbol", ["arguments"] = Arguments(".", "Calculate", snapshotId: "") }),
            Request(12, "tools/call", new JsonObject { ["name"] = "find_symbol", ["arguments"] = Arguments(".", "Calculate", limit: 0) }),
            Request(13, "tools/call", new JsonObject { ["name"] = "find_symbol", ["arguments"] = Arguments(".", "Calculate", offset: -1) }),
            Request(14, "tools/call", new JsonObject { ["name"] = "find_symbol", ["arguments"] = Arguments("elsewhere", "Calculate") }),
            """not-json""",
            Request(15, "tools/list")
        };
        try
        {
            Directory.SetCurrentDirectory(root);
            // Act
            var responses = await Talk(solution, lines, beforeLine: index =>
            {
                if (index == 2)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
                    File.WriteAllText(manifest, "{");
                }
                if (index == 4) File.WriteAllText(manifest, """{"manifestVersion":99}""");
            });

            // Assert
            var missing = Envelope(responses[0], isError: false);
            Assert.Equal("find_symbol", missing["command"]!.GetValue<string>());
            Assert.Empty(missing["result"]!["symbols"]!.AsArray());
            Assert.Equal("unknown", missing["freshness"]!["state"]!.GetValue<string>());
            Assert.False(missing["freshness"]!["isStale"]!.GetValue<bool>());
            Assert.Equal("no-index", missing["freshness"]!["inputCoverage"]!["scope"]!.GetValue<string>());
            Assert.Equal("tracked-input-content-hashes", missing["freshness"]!["scope"]!.GetValue<string>());
            Assert.Equal(new[] { "no-index" }, missing["coverage"]!["limitations"]!.AsArray().Select(item => item!.GetValue<string>()));
            Assert.Equal("unknown", missing["coverage"]!["templateCoverage"]!.GetValue<string>());
            Assert.Equal(0, missing["warningCount"]!.GetValue<int>());
            Assert.Null(missing["pagination"]!["snapshotId"]);
            Assert.False(missing["pagination"]!["continuationSupported"]!.GetValue<bool>());
            Assert.Equal(0, missing["pagination"]!["returned"]!.GetValue<int>());
            Assert.Equal(100, missing["pagination"]!["limit"]!.GetValue<int>());

            var replaced = Envelope(responses[1], isError: true);
            Assert.Equal("snapshot_mismatch", replaced["error"]!["code"]!.GetValue<string>());
            Assert.Null(replaced["freshness"]);
            Assert.Null(replaced["coverage"]);
            Assert.False(replaced.ContainsKey("pagination"));

            Assert.Equal("invalid_index", Envelope(responses[2], isError: true)["error"]!["code"]!.GetValue<string>());
            Assert.Equal("find_symbol", responses[3]["result"]!["tools"]![0]!["name"]!.GetValue<string>());
            Assert.Equal("unsupported_index", Envelope(responses[4], isError: true)["error"]!["code"]!.GetValue<string>());
            Assert.Equal("Unsupported manifest version. Rebuild the index.", Envelope(responses[4], isError: true)["error"]!["message"]!.GetValue<string>());
            Assert.Equal("find_symbol", responses[5]["result"]!["tools"]![0]!["name"]!.GetValue<string>());
            Assert.Equal("name must not be empty.", Envelope(responses[6], true)["error"]!["message"]!.GetValue<string>());
            Assert.Equal("kind must not be empty.", Envelope(responses[7], true)["error"]!["message"]!.GetValue<string>());
            Assert.Equal("Unknown symbol kind.", Envelope(responses[8], true)["error"]!["message"]!.GetValue<string>());
            Assert.Equal("project must not be empty.", Envelope(responses[9], true)["error"]!["message"]!.GetValue<string>());
            Assert.Equal("snapshotId must not be empty.", Envelope(responses[10], true)["error"]!["message"]!.GetValue<string>());
            Assert.Equal("limit must be an integer from 1 through 1000.", Envelope(responses[11], true)["error"]!["message"]!.GetValue<string>());
            Assert.Equal("offset must be a nonnegative integer.", Envelope(responses[12], true)["error"]!["message"]!.GetValue<string>());
            Assert.Equal("workspace must be the solution directory.", Envelope(responses[13], true)["error"]!["message"]!.GetValue<string>());
            Assert.Equal(-32700, responses[14]["error"]!["code"]!.GetValue<int>());
            Assert.Equal("find_symbol", responses[15]["result"]!["tools"]![0]!["name"]!.GetValue<string>());
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task McpCommandReadsAJsonRpcLine()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("codeatlas-mcp-cmd-").FullName;
        var solution = Path.Combine(root, "Sample.slnx");
        File.Copy(Path.Combine(Repository(), "tests", "Fixtures", "Sample.slnx"), solution);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var input = new StringReader(Request(1, "initialize", new JsonObject { ["protocolVersion"] = "2025-06-18" }) + "\n");

        // Act
        var code = await McpHost.RunAsync(["mcp", "--solution", solution], input, stdout, stderr, CancellationToken.None);

        // Assert
        Assert.Equal(0, code);
        Assert.Empty(stderr.ToString());
        Assert.Equal("2025-06-18", JsonNode.Parse(stdout.ToString())!["result"]!["protocolVersion"]!.GetValue<string>());
    }
}

static class McpLines
{
    internal static readonly Regex Stamp = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", RegexOptions.CultureInvariant);

    internal static async Task<JsonObject[]> Talk(string solution, IReadOnlyList<string> lines, Action<int>? beforeLine = null)
    {
        var host = McpHost.Start(solution);
        var output = new StringWriter();
        await McpHost.ServeAsync(new ScriptedInput(lines, beforeLine), output, host);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
    }

    internal static JsonObject Envelope(JsonObject response, bool isError)
    {
        Assert.Equal("2.0", response["jsonrpc"]!.GetValue<string>());
        Assert.Equal(isError, response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!;
        Assert.Equal("text", text["type"]!.GetValue<string>());
        var envelope = JsonNode.Parse(text["text"]!.GetValue<string>())!.AsObject();
        Assert.Equal(new[] { "contractVersion", "command", "success", "responseToolVersion", "warningCount", "freshness", "coverage" },
            envelope.Select(property => property.Key).Take(7));
        Assert.Equal(1, envelope["contractVersion"]!.GetValue<int>());
        Assert.Equal(CodeAtlasVersion.Current, envelope["responseToolVersion"]!.GetValue<string>());
        Assert.Equal(!isError, envelope["success"]!.GetValue<bool>());
        return envelope;
    }

    internal static void AssertShape(JsonObject envelope, bool success)
    {
        if (success)
            Assert.Equal(new[] { "contractVersion", "command", "success", "responseToolVersion", "warningCount", "freshness", "coverage", "pagination", "result" },
                envelope.Select(property => property.Key));
    }

    internal static string Request(int id, string method, JsonObject? parameters = null)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) request["params"] = parameters;
        return request.ToJsonString();
    }

    internal static string Call(int id, string workspace, string name, string? kind = null, string? project = null,
        int? offset = null, int? limit = null, string? snapshotId = null) =>
        Request(id, "tools/call", new JsonObject { ["name"] = "find_symbol", ["arguments"] = Arguments(workspace, name, kind, project, offset, limit, snapshotId) });

    internal static JsonObject Arguments(string workspace, string name, string? kind = null, string? project = null,
        int? offset = null, int? limit = null, string? snapshotId = null)
    {
        var arguments = new JsonObject { ["workspace"] = workspace, ["name"] = name };
        if (kind is not null) arguments["kind"] = kind;
        if (project is not null) arguments["project"] = project;
        if (offset is not null) arguments["offset"] = offset;
        if (limit is not null) arguments["limit"] = limit;
        if (snapshotId is not null) arguments["snapshotId"] = snapshotId;
        return arguments;
    }

    internal static async Task<CliResult> RunCli(params string[] arguments)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(Repository(), "src", "CodeAtlas.Cli", "bin", configuration, "net10.0", "codeatlas.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await output;
        var stderr = await error;
        return new(process.ExitCode, JsonNode.Parse(process.ExitCode == 0 ? stdout : stderr)!.AsObject());
    }

    internal static string Repository()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CodeAtlas.slnx")))
            repository = repository.Parent;
        return repository!.FullName;
    }

    internal sealed record CliResult(int Exit, JsonObject Json);

    private sealed class ScriptedInput(IReadOnlyList<string> lines, Action<int>? beforeLine) : TextReader
    {
        private int index;

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
        {
            if (index >= lines.Count) return new((string?)null);
            beforeLine?.Invoke(index);
            return new(lines[index++]);
        }
    }
}

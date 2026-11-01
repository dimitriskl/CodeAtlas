using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using static CodeAtlas.Tests.McpLines;

namespace CodeAtlas.Tests;

/// <summary>Raw bytes through real <c>mcp</c> and <c>live</c> processes. Stdin is written byte for byte and stdout
/// is read back from a file, because the process helper's reader would hide a byte order mark.</summary>
// Task #04: MCP tool search_symbols. Each inherited encoding is set on the child's own console (Windows code page)
// or locale charset (Unix), so these tests prove the decoding on every OS. Without a hidden console of its own the
// child would change the console of the test run.
public sealed class ProtocolEncodingTests(ProtocolEncodingFixture fixture) : IClassFixture<ProtocolEncodingFixture>
{
    private const string Phrase = "ζεστό καφέ";
    private const string Name = "Größe";
    private const string SingleByteCodePage = "437";
    private const string SingleByteLocale = "en_US.ISO-8859-1";
    private const string InvalidSlot = "invalid-bytes-slot";
    private const string PrepareId = "Library/Library.csproj::M:Sample.Brew.Prepare";
    private const string NameId = "Library/Library.csproj::M:Sample.Brew." + Name;

    private static readonly byte[] NewLine = "\n"u8.ToArray();
    private static readonly byte[] ByteOrderMark = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions RawText = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Theory]
    [InlineData("437", "en_US.ISO-8859-1")]
    [InlineData("737", "el_GR.ISO-8859-7")]
    [InlineData("65000", "en_US.UTF-16")]
    [InlineData("65001", "en_US.UTF-8")]
    public async Task McpAnswersRawTextAlikeUnderEveryInheritedEncoding(string codePage, string locale)
    {
        // Arrange
        var input = Lines(McpSearch(1, Phrase), McpFind(2));

        // Act
        var server = await RunAsync("mcp", fixture.Solution, input, codePage, locale);

        // Assert
        var responses = Responses(server);
        Assert.Equal(2, responses.Length);
        var search = Envelope(responses[0], isError: false)["result"]!;
        Assert.Equal(new[] { "ζεστό", "καφέ" }, search["queryTokens"]!.AsArray().Select(token => token!.GetValue<string>()));
        var row = Assert.Single(search["symbols"]!.AsArray())!;
        Assert.Equal(PrepareId, row["symbol"]!["id"]!.GetValue<string>());
        Assert.Contains(Phrase, row["symbol"]!["documentation"]!.ToJsonString(RawText));
        var found = Assert.Single(Envelope(responses[1], isError: false)["result"]!["symbols"]!.AsArray())!;
        Assert.Equal(NameId, found["id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("737", "el_GR.ISO-8859-7")]
    [InlineData("65000", "en_US.UTF-16")]
    public async Task LiveAnswersRawTextAlikeUnderEveryInheritedEncoding(string codePage, string locale)
    {
        // Arrange
        var input = Lines(LiveSearch(Phrase), LiveFind());

        // Act
        var server = await RunAsync("live", fixture.Solution, input, codePage, locale);

        // Assert
        var responses = Responses(server);
        Assert.Equal(2, responses.Length);
        var row = Assert.Single(responses[0]["symbols"]!.AsArray())!;
        Assert.Equal(PrepareId, row["symbol"]!["id"]!.GetValue<string>());
        Assert.Contains(Phrase, row["symbol"]!["documentation"]!.ToJsonString(RawText));
        var found = Assert.Single(responses[1]["symbols"]!.AsArray())!;
        Assert.Equal(NameId, found["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task McpReadsARawWorkspacePath()
    {
        // Arrange
        var root = Directory.CreateDirectory(Path.Combine(fixture.Parent, "καφές-" + Name)).FullName;
        var solution = Path.Combine(root, "Sample.slnx");
        File.Copy(fixture.Solution, solution);
        var input = Lines(Search(1, Phrase, root));

        // Act
        var server = await RunAsync("mcp", solution, input, SingleByteCodePage, SingleByteLocale);

        // Assert
        var result = Envelope(Assert.Single(Responses(server)), isError: false)["result"]!;
        Assert.Equal("abstained-no-index", result["decision"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("mcp")]
    [InlineData("live")]
    public async Task ALeadingByteOrderMarkDoesNotBreakTheFirstRequest(string command)
    {
        // Arrange
        var request = command == "mcp" ? McpSearch(1, Phrase) : LiveSearch(Phrase);
        byte[] input = [.. ByteOrderMark, .. Lines(request)];

        // Act
        var server = await RunAsync(command, fixture.Solution, input, SingleByteCodePage, SingleByteLocale);

        // Assert
        var response = Assert.Single(Responses(server));
        var result = command == "mcp" ? Envelope(response, isError: false)["result"]! : response;
        Assert.Equal(PrepareId, Assert.Single(result["symbols"]!.AsArray())!["symbol"]!["id"]!.GetValue<string>());
    }

    // FF FE is a UTF-16 byte order mark, so it also proves the stream start is not sniffed for another encoding.
    [Theory]
    [InlineData("FF")]
    [InlineData("FFFE")]
    [InlineData("C0AF")]
    [InlineData("EDA080")]
    [InlineData("E282")]
    public async Task McpRefusesALineWithInvalidBytesAndKeepsReading(string invalidHex)
    {
        // Arrange
        var input = InvalidLines(invalidHex, McpSearch(1, InvalidSlot), McpSearch(2, Phrase + " �"));

        // Act
        var server = await RunAsync("mcp", fixture.Solution, input, SingleByteCodePage, SingleByteLocale);

        // Assert
        var responses = Responses(server);
        Assert.Equal(3, responses.Length);
        foreach (var refused in responses[..2])
        {
            Assert.Null(refused["id"]);
            Assert.Equal(-32700, refused["error"]!["code"]!.GetValue<int>());
            Assert.Equal("Parse error", refused["error"]!["message"]!.GetValue<string>());
        }
        var result = Envelope(responses[2], isError: false)["result"]!;
        Assert.Equal(PrepareId, Assert.Single(result["symbols"]!.AsArray())!["symbol"]!["id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("FFFE")]
    [InlineData("E282")]
    public async Task LiveRefusesALineWithInvalidBytesAndKeepsReading(string invalidHex)
    {
        // Arrange
        var input = InvalidLines(invalidHex, LiveSearch(InvalidSlot), LiveSearch(Phrase + " �"));

        // Act
        var server = await RunAsync("live", fixture.Solution, input, SingleByteCodePage, SingleByteLocale);

        // Assert
        var responses = Responses(server);
        Assert.Equal(3, responses.Length);
        foreach (var refused in responses[..2])
        {
            Assert.False(refused["success"]!.GetValue<bool>());
            Assert.Null(refused["command"]);
            Assert.Equal("invalid_arguments", refused["error"]!["code"]!.GetValue<string>());
            Assert.Equal("The request line is not a JSON object.", refused["error"]!["message"]!.GetValue<string>());
        }
        Assert.Equal(PrepareId, Assert.Single(responses[2]["symbols"]!.AsArray())!["symbol"]!["id"]!.GetValue<string>());
    }

    private string McpSearch(int id, string query) => Search(id, query, fixture.Root);

    private string McpFind(int id) => Tool(id, "find_symbol", new JsonObject { ["workspace"] = fixture.Root, ["name"] = Name, ["kind"] = "method" });

    private static string Search(int id, string query, string workspace) =>
        Tool(id, "search_symbols", new JsonObject { ["workspace"] = workspace, ["query"] = query });

    private static string LiveSearch(string query) => new JsonObject { ["command"] = "search", ["query"] = query }.ToJsonString();

    private static string LiveFind() => new JsonObject { ["command"] = "find-symbol", ["name"] = Name }.ToJsonString();

    // Tool and JsonObject escape non-ASCII text, so each request is written again with its text raw.
    private static byte[] Raw(string line) => Encoding.UTF8.GetBytes(JsonNode.Parse(line)!.ToJsonString(RawText));

    private static byte[] Lines(params string[] requests) =>
        [.. requests.SelectMany(request => (byte[])[.. Raw(request), .. NewLine])];

    // Line one is only the invalid bytes, at the very start of stdin. Line two has them inside a string that would
    // otherwise be valid. Line three is valid.
    private static byte[] InvalidLines(string invalidHex, string withSlot, string valid)
    {
        var invalid = Convert.FromHexString(invalidHex);
        var slot = Encoding.UTF8.GetBytes(InvalidSlot);
        var bytes = Raw(withSlot);
        var at = bytes.AsSpan().IndexOf(slot);
        return [.. invalid, .. NewLine, .. bytes[..at], .. invalid, .. bytes[(at + slot.Length)..], .. NewLine, .. Lines(valid)];
    }

    private async Task<Server> RunAsync(string command, string solution, byte[] input, string codePage, string locale)
    {
        var output = Path.Combine(fixture.Parent, Guid.NewGuid().ToString("N") + ".stdout");
        var start = Start(command, Path.GetFileName(solution), output, codePage, locale);
        start.WorkingDirectory = Path.GetDirectoryName(solution)!;
        start.RedirectStandardInput = true;
        start.StandardInputEncoding = Encoding.Latin1;
        start.CreateNoWindow = true;
        var process = await ChildProcess.RunAsync(start, input: Encoding.Latin1.GetString(input));
        return new(process.ExitCode, process.Error, await File.ReadAllBytesAsync(output));
    }

    private static ProcessStartInfo Start(string command, string solution, string output, string codePage, string locale)
    {
        var cli = Cli();
        if (OperatingSystem.IsWindows())
            return new ProcessStartInfo("cmd") { Arguments = $"/d /c \"chcp {codePage} >nul && dotnet \"{cli}\" {command} --solution \"{solution}\" >\"{output}\"\"" };
        var start = new ProcessStartInfo("sh");
        start.Environment["LC_ALL"] = locale;
        foreach (var argument in new[] { "-c", "exec dotnet \"$0\" \"$1\" --solution \"$2\" >\"$3\"", cli, command, solution, output })
            start.ArgumentList.Add(argument);
        return start;
    }

    private static string Cli()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.Combine(Repository(), "src", "CodeAtlas.Cli", "bin", configuration, "net10.0", "codeatlas.dll");
    }

    // Strict decoding throws on anything but UTF-8, and the first byte of a response is its opening brace.
    private static JsonObject[] Responses(Server server)
    {
        Assert.True(server.ExitCode == 0, server.Error);
        Assert.Equal((byte)'{', server.Output[0]);
        return StrictUtf8.GetString(server.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
    }

    private sealed record Server(int ExitCode, string Error, byte[] Output);
}

/// <summary>The sample solution with a method documented in Greek and a method named outside ASCII.</summary>
public sealed class ProtocolEncodingFixture : IAsyncLifetime
{
    private readonly IndexedSolution isolated = new();

    public string Parent => isolated.Parent;
    public string Root => isolated.Root;
    public string Solution => isolated.Index.Solution;

    public async Task InitializeAsync()
    {
        await isolated.InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(Root, "Library", "Brew.cs"), """
            namespace Sample;
            public static class Brew
            {
                /// <summary>Φτιάχνει ζεστό καφέ για το πρωινό.</summary>
                public static int Prepare() => 1;
                public static int Größe() => 2;
            }
            """);
        await IndexStore.SaveAsync(await SolutionAnalysis.CreateIndexAsync(Solution));
    }

    public Task DisposeAsync() => isolated.DisposeAsync();
}

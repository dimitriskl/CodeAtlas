using System.Text.Json.Nodes;
using Xunit;
using static CodeAtlas.Tests.McpLines;

namespace CodeAtlas.Tests;

/// <summary>Lines that are valid JSON but cannot be read, sent to real <c>mcp</c> and <c>live</c> processes.</summary>
// Task #17: a line that ends the server has to show as an exit code and a missing answer, which a host
// called in this process cannot show. The lines are written by hand, because JsonNode refuses to write them.
public sealed class McpUnreadableLineTests(IndexedSolution fixture) : IClassFixture<IndexedSolution>
{
    private const int EchoedId = 7;
    private const int NextId = 99;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const string Unreadable = "The request line has an unpaired surrogate or a repeated key.";

    private string Solution => Path.Combine(fixture.Root, "Sample.slnx");

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":7,"method":"\ud800"}""")]
    [InlineData("""{"id":7,"method":"\udc00"}""")]
    [InlineData("""{"id":7,"method":"a\ud800b"}""")]
    [InlineData("""{"id":7,"method":"\ud800\ud800"}""")]
    [InlineData("""{"jsonrpc":"\ud800","id":7,"method":"ping"}""")]
    [InlineData("""{"id":7,"method":"ping","extra":"\ud800"}""")]
    [InlineData("""{"id":7,"method":"ping","extra":{"inner":["\ud800"]}}""")]
    [InlineData("""{"id":7,"method":"ping","extra":{"\ud800":1}}""")]
    [InlineData("""{"\ud800":1,"id":7,"method":"ping"}""")]
    [InlineData("""{"id":7,"method":"ping","method":"ping"}""")]
    [InlineData("""{"id":7,"method":"ping","\u006dethod":"ping"}""")]
    [InlineData("""{"id":7,"method":"ping","params":{},"params":{}}""")]
    [InlineData("""{"id":7,"method":"ping","extra":{"a":1,"a":2}}""")]
    public async Task AnUnreadableLineOutsideParamsIsAnInvalidRequestWithItsId(string line)
    {
        // Act
        var server = await RunServer("mcp", Solution, line, Next());

        // Assert
        var replies = Replies(server, expected: 2);
        AssertRefused(replies[0], EchoedId, InvalidRequest, "Invalid Request");
    }

    [Fact]
    public async Task AStringIdIsWrittenBackWhenOnlyTheMethodIsUnreadable()
    {
        // Arrange
        const string line = """{"id":"seven","method":"\ud800"}""";

        // Act
        var server = await RunServer("mcp", Solution, line, Next());

        // Assert
        var reply = Replies(server, expected: 2)[0];
        Assert.Equal("seven", reply["id"]!.GetValue<string>());
        Assert.Equal(InvalidRequest, reply["error"]!["code"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("""{"id":7,"method":"tools/call","params":{"name":"\ud800"}}""")]
    [InlineData("""{"id":7,"method":"tools/call","params":{"name":"\udc00","arguments":{}}}""")]
    [InlineData("""{"id":7,"method":"initialize","params":{"protocolVersion":"\ud800"}}""")]
    [InlineData("""{"id":7,"method":"ping","params":{"_meta":"\ud800"}}""")]
    [InlineData("""{"id":7,"method":"ping","params":{"_meta":{"inner":["\ud800"]}}}""")]
    [InlineData("""{"id":7,"method":"tools/call","params":{"\ud800":1}}""")]
    [InlineData("""{"id":7,"method":"tools/call","params":{"name":"find_symbol","arguments":{"\ud800":1}}}""")]
    [InlineData("""{"id":7,"method":"tools/call","params":{"name":"find_symbol","arguments":"\ud800"}}""")]
    [InlineData("""{"id":7,"method":"tools/call","params":{"name":"find_symbol","arguments":["\ud800"]}}""")]
    [InlineData("""{"id":7,"method":"ping","params":"\ud800"}""")]
    [InlineData("""{"id":7,"method":"ping","params":["\ud800"]}""")]
    [InlineData("""{"id":7,"method":"tools/call","params":{"name":"coverage","name":"coverage"}}""")]
    [InlineData("""{"id":7,"method":"tools/call","params":{"name":"coverage","arguments":{"workspace":"a","workspace":"b"}}}""")]
    public async Task AnUnreadableLineInsideParamsIsInvalidParamsWithItsId(string line)
    {
        // Act
        var server = await RunServer("mcp", Solution, line, Next());

        // Assert
        var replies = Replies(server, expected: 2);
        AssertRefused(replies[0], EchoedId, InvalidParams, "Invalid params");
    }

    [Theory]
    [InlineData("""{"id":"\ud800","method":"ping"}""")]
    [InlineData("""{"id":"\udc00","method":"ping"}""")]
    [InlineData("""{"id":"\ud800"}""")]
    [InlineData("""{"id":"\ud800","method":"\ud800"}""")]
    [InlineData("""{"id":{"a":1},"method":"ping"}""")]
    [InlineData("""{"id":{"a":"\ud800"},"method":"ping"}""")]
    [InlineData("""{"id":{"\ud800":1},"method":"ping"}""")]
    [InlineData("""{"id":[1],"method":"ping"}""")]
    [InlineData("""{"id":["\ud800"],"method":"ping"}""")]
    [InlineData("""{"id":true,"method":"ping"}""")]
    [InlineData("""{"id":1,"id":2,"method":"ping"}""")]
    [InlineData("""{"id":1,"\u0069d":2,"method":"ping"}""")]
    public async Task AnIdThatCannotBeWrittenBackGetsAReplyWithANullId(string line)
    {
        // Act
        var server = await RunServer("mcp", Solution, line, Next());

        // Assert
        var replies = Replies(server, expected: 2);
        AssertRefused(replies[0], id: null, InvalidRequest, "Invalid Request");
    }

    [Theory]
    [InlineData("""{"method":"\ud800"}""")]
    [InlineData("""{"method":"notifications/changed","params":{"value":"\ud800"}}""")]
    [InlineData("""{"method":"notifications/changed","params":{"\ud800":1}}""")]
    [InlineData("""{"method":"notifications/changed","params":"\ud800"}""")]
    [InlineData("""{"method":"notifications/changed","extra":"\ud800"}""")]
    [InlineData("""{"\ud800":1}""")]
    [InlineData("""{"method":"ping","method":"ping"}""")]
    public async Task ALineWithNoIdGetsNoReply(string line)
    {
        // Act
        var server = await RunServer("mcp", Solution, line, Next());

        // Assert
        var reply = Assert.Single(Replies(server, expected: 1));
        Assert.Equal(NextId, reply["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task ASurrogatePairInMethodIsAnOrdinaryUnknownMethod()
    {
        // Arrange
        const string line = """{"id":7,"method":"\ud83d\ude00"}""";

        // Act
        var server = await RunServer("mcp", Solution, line, Next());

        // Assert
        var replies = Replies(server, expected: 2);
        AssertRefused(replies[0], EchoedId, MethodNotFound, "Method not found");
    }

    [Fact]
    public async Task ASurrogatePairInAStringIdIsWrittenBack()
    {
        // Arrange
        const string line = """{"id":"\ud83d\ude00","method":"ping"}""";

        // Act
        var server = await RunServer("mcp", Solution, line, Next());

        // Assert
        var reply = Replies(server, expected: 2)[0];
        Assert.Equal("\ud83d\ude00", reply["id"]!.GetValue<string>());
        Assert.NotNull(reply["result"]);
    }

    [Fact]
    public async Task ASurrogatePairInToolNameIsAnOrdinaryUnknownTool()
    {
        // Arrange
        const string line = """{"id":7,"method":"tools/call","params":{"name":"\ud83d\ude00"}}""";

        // Act
        var server = await RunServer("mcp", Solution, line, Next());

        // Assert
        var unknown = Envelope(Replies(server, expected: 2)[0], isError: true);
        Assert.Equal("\ud83d\ude00", unknown["command"]!.GetValue<string>());
        Assert.Equal("invalid_arguments", unknown["error"]!["code"]!.GetValue<string>());
        Assert.Equal("Unknown tool.", unknown["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task ASurrogatePairInProtocolVersionGetsTheServersVersion()
    {
        // Arrange
        const string line = """{"id":7,"method":"initialize","params":{"protocolVersion":"\ud83d\ude00"}}""";

        // Act
        var server = await RunServer("mcp", Solution, line, Next());

        // Assert
        var reply = Replies(server, expected: 2)[0];
        Assert.Equal("2025-06-18", reply["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"\ud83d\ude00":1,"id":7,"method":"ping"}""")]
    [InlineData("""{"id":7,"method":"ping","extra":"\ud83d\ude00"}""")]
    [InlineData("""{"id":7,"method":"ping","params":{"_meta":"\ud83d\ude00"}}""")]
    public async Task ASurrogatePairElsewhereIsOrdinaryText(string line)
    {
        // Act
        var server = await RunServer("mcp", Solution, line, Next());

        // Assert
        var reply = Replies(server, expected: 2)[0];
        Assert.Equal(EchoedId, reply["id"]!.GetValue<int>());
        Assert.NotNull(reply["result"]);
    }

    [Theory]
    [InlineData("""{"id":null,"method":"ping"}""", "\"id\":null")]
    [InlineData("""{"id":1.5,"method":"ping"}""", "\"id\":1.5")]
    [InlineData("""{"id":12345678901234567890,"method":"ping"}""", "\"id\":12345678901234567890")]
    public async Task ANullOrNumericIdIsWrittenBackAsSent(string line, string written)
    {
        // Act
        var server = await RunServer("mcp", Solution, line, Next());

        // Assert
        var reply = Replies(server, expected: 2)[0];
        Assert.NotNull(reply["result"]);
        Assert.Contains(written, server.Output, StringComparison.Ordinal);
    }

    // Task #04's rule still holds on a real server: the tool answers for a value inside arguments.
    [Fact]
    public async Task AnUnpairedSurrogateInAToolArgumentStaysTheToolsAnswer()
    {
        // Arrange
        var line = RawTool(EchoedId, "find_symbol", Arguments(fixture.Root, "Calculate"), "name", "\"\\ud800\"");

        // Act
        var server = await RunServer("mcp", Solution, line, Next());

        // Assert
        var invalid = Envelope(Replies(server, expected: 2)[0], isError: true);
        Assert.Equal("find_symbol", invalid["command"]!.GetValue<string>());
        Assert.Equal("invalid_arguments", invalid["error"]!["code"]!.GetValue<string>());
        Assert.Equal("name must not be empty.", invalid["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task AToolIgnoresAnUnpairedSurrogateInAnArgumentItDoesNotRead()
    {
        // Arrange
        var line = RawTool(EchoedId, "find_symbol", Arguments(fixture.Root, "Calculate"), "extra", "\"\\ud800\"");

        // Act
        var server = await RunServer("mcp", Solution, line, Next());

        // Assert
        var found = Envelope(Replies(server, expected: 2)[0], isError: false);
        Assert.NotEmpty(found["result"]!["symbols"]!.AsArray());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"id":"\ud80""")]
    public async Task ALineThatIsNotJsonStaysAParseError(string line)
    {
        // Act
        var server = await RunServer("mcp", Solution, line, Next());

        // Assert
        var replies = Replies(server, expected: 2);
        AssertRefused(replies[0], id: null, -32700, "Parse error");
    }

    [Theory]
    [InlineData("""{"id":7}""")]
    [InlineData("""{"id":7,"method":5}""")]
    [InlineData("""{"id":7,"method":""}""")]
    public async Task AReadableMethodThatIsNoMethodNameStaysAnInvalidRequestWithItsId(string line)
    {
        // Act
        var server = await RunServer("mcp", Solution, line, Next());

        // Assert
        var replies = Replies(server, expected: 2);
        AssertRefused(replies[0], EchoedId, InvalidRequest, "Invalid Request");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""["\ud800"]""")]
    [InlineData("\"\\ud800\"")]
    public async Task ValidJsonThatIsNotAnObjectStaysAnInvalidRequest(string line)
    {
        // Act
        var server = await RunServer("mcp", Solution, line, Next());

        // Assert
        var replies = Replies(server, expected: 2);
        AssertRefused(replies[0], id: null, InvalidRequest, "Invalid Request");
    }

    // One process for every row, because live loads the solution when it starts.
    [Fact]
    public async Task LiveRefusesAnUnreadableLineAndKeepsReading()
    {
        // Arrange
        string[] unreadable =
        [
            """{"command":"\ud800"}""",
            """{"\ud800":1}""",
            """{"command":"find-symbol","name":"\ud800"}""",
            """{"command":"find-symbol","name":"Calculate","extra":"\ud800"}""",
            """{"command":"find-symbol","name":"Calculate","extra":{"\ud800":1}}""",
            """{"command":"find-symbol","command":"find-symbol","name":"Calculate"}""",
            """{"command":"edit","path":"Library/Calculator.cs","text":"\ud800"}"""
        ];
        string[] lines = [.. unreadable, """{"command":"find-symbol","name":"Calculate"}"""];

        // Act
        var server = await RunServer("live", Solution, lines);

        // Assert
        var replies = Replies(server, expected: lines.Length);
        foreach (var refused in replies[..^1])
        {
            Assert.False(refused["success"]!.GetValue<bool>());
            Assert.Null(refused["command"]);
            Assert.Equal("invalid_arguments", refused["error"]!["code"]!.GetValue<string>());
            Assert.Equal(Unreadable, refused["error"]!["message"]!.GetValue<string>());
        }
        Assert.NotEmpty(replies[^1]["symbols"]!.AsArray());
    }

    [Fact]
    public async Task LiveStillRefusesValidJsonThatIsNotAnObject()
    {
        // Arrange
        string[] lines = ["null", """["\ud800"]""", """{"command":"find-symbol","name":"Calculate"}"""];

        // Act
        var server = await RunServer("live", Solution, lines);

        // Assert
        var replies = Replies(server, expected: lines.Length);
        foreach (var refused in replies[..^1])
        {
            Assert.Equal("invalid_arguments", refused["error"]!["code"]!.GetValue<string>());
            Assert.Equal("A request must be a JSON object.", refused["error"]!["message"]!.GetValue<string>());
        }
        Assert.NotEmpty(replies[^1]["symbols"]!.AsArray());
    }

    [Fact]
    public async Task LiveTreatsASurrogatePairAsOrdinaryText()
    {
        // Arrange
        const string line = """{"command":"find-symbol","name":"\ud83d\ude00"}""";

        // Act
        var server = await RunServer("live", Solution, line);

        // Assert
        var reply = Assert.Single(Replies(server, expected: 1));
        Assert.True(reply["success"]!.GetValue<bool>());
        Assert.Empty(reply["symbols"]!.AsArray());
    }

    private static string Next() => Request(NextId, "tools/list");

    // The server read to the end of stdin and left: every line is answered, and nothing went to stderr.
    private static JsonObject[] Replies(ChildProcess.Result server, int expected)
    {
        Assert.True(server.ExitCode == 0, server.Error);
        Assert.Equal("", server.Error);
        var replies = server.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        Assert.Equal(expected, replies.Length);
        return replies;
    }

    private static void AssertRefused(JsonObject reply, int? id, int code, string message)
    {
        Assert.Equal("2.0", reply["jsonrpc"]!.GetValue<string>());
        Assert.True(reply.ContainsKey("id"));
        Assert.Equal(id, reply["id"]?.GetValue<int>());
        Assert.Equal(code, reply["error"]!["code"]!.GetValue<int>());
        Assert.Equal(message, reply["error"]!["message"]!.GetValue<string>());
        Assert.False(reply.ContainsKey("result"));
    }
}

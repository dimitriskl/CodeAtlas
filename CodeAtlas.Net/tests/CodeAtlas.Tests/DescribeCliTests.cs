using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class DescribeCliTests(ExplanationCliContractFixture fixture)
    : IClassFixture<ExplanationCliContractFixture>
{
    private const string SymbolId = "Library/Library.csproj::M:ExplanationFixture.Numbers.Double(System.Int32)";

    [Fact]
    public async Task BoundedBatchSkipsOversizedDeclarationAndReachesSupportedMethods()
    {
        // Arrange
        var original = await File.ReadAllTextAsync(fixture.SourceFile);
        await File.WriteAllTextAsync(fixture.SourceFile, original
            + "\npublic class AAALarge { public static string Wide() => \"" + new string('x', 70_000) + "\"; }");
        try
        {
            var index = await SolutionAnalysis.CreateIndexAsync(fixture.Solution);
            await IndexStore.SaveAsync(index);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1/chat/completions";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var provider = ServeOneAsync(listener, timeout.Token);

            // Act
            var result = await RunAsync("fixture-api-secret", endpoint, "describe", "--index", fixture.IndexPath,
                "--project", "Library/Library.csproj", "--limit", "1");
            await provider;

            // Assert
            Assert.True(result.Exit == 0, result.Error);
            Assert.Equal(1, result.Json["generated"]!.GetValue<int>());
            Assert.True(result.Json["failed"]!.GetValue<int>() >= 1);
            Assert.True(File.Exists(result.Json["descriptionWarningsFile"]!.GetValue<string>()));
        }
        finally
        {
            await File.WriteAllTextAsync(fixture.SourceFile, original);
            await IndexStore.SaveAsync(await SolutionAnalysis.CreateIndexAsync(fixture.Solution));
        }
    }

    [Fact]
    public async Task OptionalProviderDescriptionsAreSavedResumedAndSearchableWithoutCredentials()
    {
        // Arrange
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1/chat/completions";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var provider = ServeOneAsync(listener, timeout.Token);
        var store = Path.Combine(fixture.Root, ".codeatlas", "explanations");

        // Act
        var disabled = await RunAsync(null, endpoint, "describe", "--index", fixture.IndexPath);
        var described = await RunAsync("fixture-api-secret", endpoint, "describe", "--index", fixture.IndexPath, "--symbol", SymbolId);
        var providerAuthorization = await provider;
        var resumed = await RunAsync("fixture-api-secret", endpoint, "describe", "--index", fixture.IndexPath, "--symbol", SymbolId);
        var assisted = await RunAsync(null, endpoint, "search-symbols", "--index", fixture.IndexPath, "--query", "arithmetically");
        var names = await RunAsync(null, endpoint, "search-symbols", "--index", fixture.IndexPath, "--query", "arithmetically", "--mode", "names");
        var storedText = string.Join("\n", Directory.GetFiles(store, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText));

        // Assert
        Assert.Equal(0, disabled.Exit);
        Assert.Equal("disabled", disabled.Json["ai"]!.GetValue<string>());
        Assert.Equal(0, disabled.Json["generated"]!.GetValue<int>());
        Assert.Equal("Bearer fixture-api-secret", providerAuthorization);
        Assert.Equal(0, described.Exit);
        Assert.Equal(1, described.Json["generated"]!.GetValue<int>());
        Assert.Equal(0, resumed.Exit);
        Assert.Equal(0, resumed.Json["generated"]!.GetValue<int>());
        Assert.Equal(1, resumed.Json["cached"]!.GetValue<int>());
        Assert.Equal(0, assisted.Exit);
        var match = Assert.Single(assisted.Json["symbols"]!.AsArray());
        Assert.Equal(SymbolId, match!["symbol"]!["id"]!.GetValue<string>());
        Assert.Contains(match["reasons"]!.AsArray(), reason => reason!["field"]!.GetValue<string>() == "ai-description");
        Assert.Equal(0, names.Exit);
        Assert.Empty(names.Json["symbols"]!.AsArray());
        Assert.DoesNotContain("fixture-api-secret", storedText, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-api-secret", described.Output, StringComparison.Ordinal);
    }

    private static async Task<string> ServeOneAsync(TcpListener listener, CancellationToken token)
    {
        using var connection = await listener.AcceptTcpClientAsync(token);
        await using var stream = connection.GetStream();
        using var headers = new MemoryStream();
        var one = new byte[1];
        while (headers.Length < 16_384)
        {
            if (await stream.ReadAsync(one, token) != 1) throw new IOException("Incomplete HTTP headers.");
            headers.WriteByte(one[0]);
            var bytes = headers.GetBuffer();
            var n = (int)headers.Length;
            if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) break;
        }
        var lines = Encoding.ASCII.GetString(headers.ToArray()).Split("\r\n");
        var length = int.Parse(lines.Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1]);
        if (length is < 1 or > 524_288) throw new IOException("Unexpected request size.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token);
        using var request = JsonDocument.Parse(payload);
        using var evidence = JsonDocument.Parse(request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        var excerpt = evidence.RootElement.GetProperty("evidence").EnumerateArray().First(item => item.GetProperty("role").GetString() == "selected-declaration");
        var excerptText = excerpt.GetProperty("text").GetString()!;
        var quote = excerptText.Contains("value * 2", StringComparison.Ordinal) ? "value * 2"
            : excerptText[..Math.Min(80, excerptText.Length)];
        var content = JsonSerializer.Serialize(new
        {
            claims = new[] { new { text = "Scales an integer arithmetically by a factor of two.", evidence = new[] {
                new { evidenceId = excerpt.GetProperty("id").GetString(), quote } } } },
            limitations = Array.Empty<string>()
        });
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = "fixture-model", choices = new[] { new { finish_reason = "stop", message = new { content } } }
        });
        var responseHeaders = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(responseHeaders, token);
        await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
        return lines.Single(line => line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
    }

    private static async Task<Result> RunAsync(string? key, string endpoint, params string[] arguments)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CodeAtlas.slnx"))) repository = repository.Parent;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(repository!.FullName, "src", "CodeAtlas.Cli", "bin", configuration, "net10.0", "codeatlas.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.Environment.Remove("CODEATLAS_AI_API_KEY");
        if (key is not null) start.Environment["CODEATLAS_AI_API_KEY"] = key;
        start.Environment["CODEATLAS_AI_MODEL"] = "fixture-model";
        start.Environment["CODEATLAS_AI_ENDPOINT"] = endpoint;
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode, await stdout, await stderr);
    }

    private sealed record Result(int Exit, string Output, string Error)
    {
        public JsonObject Json => JsonNode.Parse(Exit == 0 ? Output : Error)!.AsObject();
    }
}

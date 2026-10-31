using System.Text.Json.Nodes;
using Xunit;
using static CodeAtlas.Tests.McpLines;

namespace CodeAtlas.Tests;

public sealed class McpWarmIndexTests(IndexedSolution fixture) : IClassFixture<IndexedSolution>
{
    private string Solution => Path.Combine(fixture.Root, "Sample.slnx");
    private string Calculator => Path.Combine(fixture.Root, "Library", "Calculator.cs");

    [Fact]
    public async Task SecondFindSymbolReturnsTheSameRowsWithoutHashingUnchangedInputsAgain()
    {
        // Arrange
        FileStream? held = null;
        SnapshotInputs.ResetFullFreshnessChecks(Solution);
        var lines = new[] { Call(1, fixture.Root, "Calculate"), Call(2, fixture.Root, "Calculate") };
        try
        {
            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                // A hash of this file would now fail, so a successful answer proves it was not read.
                if (index == 1) held = new FileStream(Calculator, FileMode.Open, FileAccess.Read, FileShare.None);
            });

            // Assert
            var first = Envelope(responses[0], isError: false);
            var second = Envelope(responses[1], isError: false);
            Assert.NotEmpty(first["result"]!["symbols"]!.AsArray());
            Assert.Equal(first["result"]!.ToJsonString(), second["result"]!.ToJsonString());
            Assert.Equal(first["pagination"]!.ToJsonString(), second["pagination"]!.ToJsonString());
            Assert.False(second["freshness"]!["isStale"]!.GetValue<bool>());
            Assert.Equal(first["freshness"]!["state"]!.GetValue<string>(), second["freshness"]!["state"]!.GetValue<string>());
            Assert.Equal(1, SnapshotInputs.FullFreshnessChecks(Solution));
        }
        finally
        {
            held?.Dispose();
        }
    }

    [Fact]
    public async Task EveryToolSharesOneFullFreshnessCheck()
    {
        // Arrange
        SnapshotInputs.ResetFullFreshnessChecks(Solution);
        var workspace = new JsonObject { ["workspace"] = fixture.Root };
        var lines = new[]
        {
            Tool(1, "index_status", workspace.DeepClone().AsObject()),
            Tool(2, "coverage", workspace.DeepClone().AsObject()),
            Call(3, fixture.Root, "Calculate")
        };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        Assert.False(Envelope(responses[0], isError: false)["freshness"]!["isStale"]!.GetValue<bool>());
        Assert.False(Envelope(responses[1], isError: false)["freshness"]!["isStale"]!.GetValue<bool>());
        Assert.False(Envelope(responses[2], isError: false)["freshness"]!["isStale"]!.GetValue<bool>());
        Assert.Equal(1, SnapshotInputs.FullFreshnessChecks(Solution));
    }

    [Theory]
    [InlineData("// appended")]
    [InlineData("")]
    public async Task EditingATrackedFileAfterTheFirstCallMakesTheNextQueryStale(string appended)
    {
        // Arrange
        var original = await File.ReadAllTextAsync(Calculator);
        var edited = appended.Length > 0 ? original + appended : SwapFirstLetter(original);
        var lines = new[] { Call(1, fixture.Root, "Calculate"), Call(2, fixture.Root, "Calculate"), Request(3, "tools/list") };
        try
        {
            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                if (index == 1) File.WriteAllText(Calculator, edited);
            });

            // Assert
            Envelope(responses[0], isError: false);
            var stale = Envelope(responses[1], isError: true);
            Assert.Equal("stale_index", stale["error"]!["code"]!.GetValue<string>());
            Assert.Equal("The saved index is stale.", stale["error"]!["message"]!.GetValue<string>());
            Assert.True(stale["freshness"]!["isStale"]!.GetValue<bool>());
            Assert.Equal(new[] { "Library/Calculator.cs" }, stale["freshness"]!["changedFiles"]!.AsArray().Select(file => file!.GetValue<string>()));
            Assert.NotNull(responses[2]["result"]);
        }
        finally
        {
            await File.WriteAllTextAsync(Calculator, original);
            await fixture.RestorePublicationAsync();
        }
    }

    [Fact]
    public async Task RestoringAnEditedFileMakesTheIndexFreshAgainWithoutARestart()
    {
        // Arrange
        var original = await File.ReadAllTextAsync(Calculator);
        var lines = new[] { Call(1, fixture.Root, "Calculate"), Call(2, fixture.Root, "Calculate"), Call(3, fixture.Root, "Calculate") };
        try
        {
            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                if (index == 1) File.AppendAllText(Calculator, "// changed");
                if (index == 2) File.WriteAllText(Calculator, original);
            });

            // Assert
            Assert.Equal("stale_index", Envelope(responses[1], isError: true)["error"]!["code"]!.GetValue<string>());
            Assert.False(Envelope(responses[2], isError: false)["freshness"]!["isStale"]!.GetValue<bool>());
        }
        finally
        {
            await File.WriteAllTextAsync(Calculator, original);
            await fixture.RestorePublicationAsync();
        }
    }

    [Fact]
    public async Task TouchingATrackedFileHashesOnlyThatFileAndStaysFresh()
    {
        // Arrange
        var original = File.GetLastWriteTimeUtc(Calculator);
        SnapshotInputs.ResetFullFreshnessChecks(Solution);
        var lines = new[] { Call(1, fixture.Root, "Calculate"), Call(2, fixture.Root, "Calculate") };
        try
        {
            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                if (index == 1) File.SetLastWriteTimeUtc(Calculator, original.AddMinutes(-5));
            });

            // Assert
            Assert.False(Envelope(responses[1], isError: false)["freshness"]!["isStale"]!.GetValue<bool>());
            Assert.Equal(1, SnapshotInputs.FullFreshnessChecks(Solution));
        }
        finally
        {
            File.SetLastWriteTimeUtc(Calculator, original);
        }
    }

    [Fact]
    public async Task AddingASourceFileMakesTheNextQueryStale()
    {
        // Arrange
        var added = Path.Combine(fixture.Root, "Library", "Added.cs");
        var lines = new[] { Call(1, fixture.Root, "Calculate"), Call(2, fixture.Root, "Calculate") };
        try
        {
            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                if (index == 1) File.WriteAllText(added, "namespace Library; public sealed class Added;");
            });

            // Assert
            Envelope(responses[0], isError: false);
            var stale = Envelope(responses[1], isError: true);
            Assert.Equal("stale_index", stale["error"]!["code"]!.GetValue<string>());
            Assert.Contains("Library/Added.cs", stale["freshness"]!["changedFiles"]!.AsArray().Select(file => file!.GetValue<string>()));
        }
        finally
        {
            File.Delete(added);
            await fixture.RestorePublicationAsync();
        }
    }

    [Fact]
    public async Task AnUnreadableChangedFileFailsAsInputVerificationInsteadOfAnInternalError()
    {
        // Arrange
        FileStream? held = null;
        var original = await File.ReadAllTextAsync(Calculator);
        var lines = new[] { Call(1, fixture.Root, "Calculate"), Call(2, fixture.Root, "Calculate") };
        try
        {
            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                if (index != 1) return;
                File.AppendAllText(Calculator, "// changed");
                held = new FileStream(Calculator, FileMode.Open, FileAccess.Read, FileShare.None);
            });

            // Assert
            Envelope(responses[0], isError: false);
            var failed = Envelope(responses[1], isError: true);
            Assert.Equal("snapshot_io_failed", failed["error"]!["code"]!.GetValue<string>());
            Assert.NotEmpty(failed["error"]!["diagnostics"]!.AsArray());
        }
        finally
        {
            held?.Dispose();
            await File.WriteAllTextAsync(Calculator, original);
            await fixture.RestorePublicationAsync();
        }
    }

    [Fact]
    public async Task PublishingThroughIndexReloadsTheNextCallAndRefusesTheOldSnapshot()
    {
        // Arrange
        var old = (await IndexStore.OpenSnapshotAsync(fixture.IndexPath)).Index.IndexId;
        var lines = new[]
        {
            Call(1, fixture.Root, "Calculate"),
            Tool(2, "index", new JsonObject { ["workspace"] = fixture.Root }),
            Call(3, fixture.Root, "Calculate"),
            Tool(4, "index_status", new JsonObject { ["workspace"] = fixture.Root }),
            Call(5, fixture.Root, "Calculate", snapshotId: old)
        };

        // Act
        var responses = await Talk(Solution, lines);

        // Assert
        var before = Envelope(responses[0], isError: false);
        var generation = Envelope(responses[1], isError: false)["result"]!["generationId"]!.GetValue<string>();
        var after = Envelope(responses[2], isError: false);
        Assert.Equal(old, before["pagination"]!["snapshotId"]!.GetValue<string>());
        Assert.NotEqual(old, generation);
        Assert.Equal(generation, after["pagination"]!["snapshotId"]!.GetValue<string>());
        Assert.Equal(before["result"]!.ToJsonString(), after["result"]!.ToJsonString());
        Assert.Equal(generation, Envelope(responses[3], isError: false)["result"]!["generationId"]!.GetValue<string>());
        var mismatch = Envelope(responses[4], isError: true);
        Assert.Equal("snapshot_mismatch", mismatch["error"]!["code"]!.GetValue<string>());
        Assert.Equal("The index was replaced. Start a new query at offset zero.", mismatch["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task PublishingOutsideTheServerReloadsWithOneFreshnessCheckPerGeneration()
    {
        // Arrange
        var lines = new[] { Call(1, fixture.Root, "Calculate"), Call(2, fixture.Root, "Calculate"), Call(3, fixture.Root, "Calculate") };
        var published = "";
        SnapshotInputs.ResetFullFreshnessChecks(Solution);

        // Act
        var responses = await Talk(Solution, lines, beforeLine: index =>
        {
            if (index != 1) return;
            fixture.RestorePublicationAsync().GetAwaiter().GetResult();
            published = fixture.Index.IndexId;
            SnapshotInputs.ResetFullFreshnessChecks(Solution);
        });

        // Assert
        Assert.Equal(published, Envelope(responses[1], isError: false)["pagination"]!["snapshotId"]!.GetValue<string>());
        Assert.Equal(published, Envelope(responses[2], isError: false)["pagination"]!["snapshotId"]!.GetValue<string>());
        Assert.Equal(1, SnapshotInputs.FullFreshnessChecks(Solution));
    }

    [Fact]
    public async Task AWarmServerDoesNotBlockRepublishingOrDeletingARetainedGeneration()
    {
        // Arrange
        var old = await IndexStore.OpenSnapshotAsync(fixture.IndexPath);
        var lines = new[] { Call(1, fixture.Root, "Calculate"), Call(2, fixture.Root, "Calculate"), Call(3, fixture.Root, "Calculate") };
        var retained = false;
        var deleted = false;

        // Act
        var responses = await Talk(Solution, lines, beforeLine: index =>
        {
            if (index == 1)
            {
                fixture.RestorePublicationAsync().GetAwaiter().GetResult();
                retained = File.Exists(Path.Combine(old.GenerationDirectory!, "index.db"));
                // The server has not been asked anything since this generation was replaced, so a
                // connection held from the first call would make this throw on Windows.
                Directory.Delete(old.GenerationDirectory!, recursive: true);
                deleted = !Directory.Exists(old.GenerationDirectory!);
            }
            if (index == 2) fixture.RestorePublicationAsync().GetAwaiter().GetResult();
        });

        // Assert
        Assert.True(retained);
        Assert.True(deleted);
        Envelope(responses[1], isError: false);
        Assert.Equal(fixture.Index.IndexId, Envelope(responses[2], isError: false)["pagination"]!["snapshotId"]!.GetValue<string>());
    }

    [Fact]
    public async Task AWarmServerDoesNotBlockDeletingTheActiveGeneration()
    {
        // Arrange
        var active = await IndexStore.OpenSnapshotAsync(fixture.IndexPath);
        var lines = new[] { Call(1, fixture.Root, "Calculate"), Call(2, fixture.Root, "Calculate"), Call(3, fixture.Root, "Calculate") };
        try
        {
            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                if (index == 1) Directory.Delete(active.GenerationDirectory!, recursive: true);
                if (index == 2) fixture.RestorePublicationAsync().GetAwaiter().GetResult();
            });

            // Assert
            Assert.Equal("invalid_index", Envelope(responses[1], isError: true)["error"]!["code"]!.GetValue<string>());
            Assert.Equal(fixture.Index.IndexId, Envelope(responses[2], isError: false)["pagination"]!["snapshotId"]!.GetValue<string>());
        }
        finally
        {
            await fixture.RestorePublicationAsync();
        }
    }

    [Fact]
    public async Task AWarmServerAnswersLikeANewOneWhenThePointerGoesMissingOrCorrupt()
    {
        // Arrange
        var pointer = await File.ReadAllBytesAsync(fixture.IndexPath);
        var published = (await IndexStore.OpenSnapshotAsync(fixture.IndexPath)).Index.IndexId;
        var lines = new[]
        {
            Call(1, fixture.Root, "Calculate"),
            Call(2, fixture.Root, "Calculate"),
            Tool(3, "index_status", new JsonObject { ["workspace"] = fixture.Root }),
            Call(4, fixture.Root, "Calculate", snapshotId: published),
            Call(5, fixture.Root, "Calculate"),
            Tool(6, "coverage", new JsonObject { ["workspace"] = fixture.Root }),
            Call(7, fixture.Root, "Calculate"),
            Call(8, fixture.Root, "Calculate")
        };

        // Act
        var responses = await Talk(Solution, lines, beforeLine: index =>
        {
            if (index == 1) File.Delete(fixture.IndexPath);
            if (index == 4) File.WriteAllText(fixture.IndexPath, "{");
            if (index == 6) File.WriteAllText(fixture.IndexPath, """{"manifestVersion":99}""");
            if (index == 7) File.WriteAllBytes(fixture.IndexPath, pointer);
        });

        // Assert
        var missing = Envelope(responses[1], isError: false);
        Assert.Empty(missing["result"]!["symbols"]!.AsArray());
        Assert.Equal("no-index", missing["freshness"]!["inputCoverage"]!["scope"]!.GetValue<string>());
        Assert.Null(missing["pagination"]!["snapshotId"]);
        Assert.Null(Envelope(responses[2], isError: false)["result"]!["generationId"]);
        Assert.Equal("snapshot_mismatch", Envelope(responses[3], isError: true)["error"]!["code"]!.GetValue<string>());
        Assert.Equal("invalid_index", Envelope(responses[4], isError: true)["error"]!["code"]!.GetValue<string>());
        Assert.Equal("invalid_index", Envelope(responses[5], isError: true)["error"]!["code"]!.GetValue<string>());
        var unsupported = Envelope(responses[6], isError: true);
        Assert.Equal("unsupported_index", unsupported["error"]!["code"]!.GetValue<string>());
        Assert.Equal("Unsupported manifest version. Rebuild the index.", unsupported["error"]!["message"]!.GetValue<string>());
        Assert.Equal(published, Envelope(responses[7], isError: false)["pagination"]!["snapshotId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ATruncatedDatabaseIsRefusedOnTheNextCall()
    {
        // Arrange
        var database = (await IndexStore.OpenSnapshotAsync(fixture.IndexPath)).IndexPath;
        var lines = new[] { Call(1, fixture.Root, "Calculate"), Call(2, fixture.Root, "Calculate") };
        try
        {
            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                if (index != 1) return;
                using var file = new FileStream(database, FileMode.Open, FileAccess.Write);
                file.SetLength(file.Length - 1);
            });

            // Assert
            Envelope(responses[0], isError: false);
            var failed = Envelope(responses[1], isError: true);
            Assert.Equal("invalid_index", failed["error"]!["code"]!.GetValue<string>());
            Assert.Equal("Snapshot artifact is missing or truncated: index.db Rebuild the index.", failed["error"]!["message"]!.GetValue<string>());
        }
        finally
        {
            await fixture.RestorePublicationAsync();
        }
    }

    [Fact]
    public async Task ADatabaseHeaderChangedAfterTheFirstCallIsRefusedOnTheNextCall()
    {
        // Arrange
        const int applicationIdOffset = 68;
        var database = (await IndexStore.OpenSnapshotAsync(fixture.IndexPath)).IndexPath;
        var lines = new[] { Call(1, fixture.Root, "Calculate"), Call(2, fixture.Root, "Calculate") };
        try
        {
            // Act
            var responses = await Talk(Solution, lines, beforeLine: index =>
            {
                if (index != 1) return;
                using var file = new FileStream(database, FileMode.Open, FileAccess.Write);
                file.Position = applicationIdOffset;
                file.Write(new byte[4]);
            });

            // Assert
            Envelope(responses[0], isError: false);
            var failed = Envelope(responses[1], isError: true);
            Assert.Equal("invalid_index", failed["error"]!["code"]!.GetValue<string>());
            Assert.Equal("The file is not a CodeAtlas index database. Rebuild the index.", failed["error"]!["message"]!.GetValue<string>());
        }
        finally
        {
            await fixture.RestorePublicationAsync();
        }
    }

    private static string SwapFirstLetter(string text)
    {
        var position = text.IndexOf('c');
        Assert.True(position >= 0);
        return text[..position] + 'k' + text[(position + 1)..];
    }
}

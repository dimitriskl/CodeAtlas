using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeAtlas.Persistence;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class GenerationStoreTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("codeatlas-generations-");
    private string Solution => Path.Combine(root.FullName, "Sample.slnx");
    private static string Id() => Guid.NewGuid().ToString("N");
    private static Task Fresh(CancellationToken _) => Task.CompletedTask;

    private static GenerationArtifact[] Artifacts(string id) =>
    [
        Text("index.json", JsonSerializer.Serialize(new { indexId = id, value = "snapshot-data" })),
        Text("overview.md", "Overview for " + id),
        Text("warnings.txt", "Index ID: " + id + "\nNo warnings.\n"),
        Text("timings.json", JsonSerializer.Serialize(new { indexId = id, seconds = 1 }))
    ];

    private static GenerationArtifact Text(string name, string text) => new(name,
        async (stream, token) => await stream.WriteAsync(Encoding.UTF8.GetBytes(text), token));

    private static async Task<string> ReadText(ResolvedGeneration generation, string name) =>
        await GenerationStore.ReadVerifiedAsync(generation, name, async (stream, token) =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, leaveOpen: true);
            return await reader.ReadToEndAsync(token);
        });

    [Fact]
    public async Task PublicationMakesFourMatchingImmutableArtifactsAvailable()
    {
        // Arrange
        var store = new GenerationStore();
        var id = Id();

        // Act
        var saved = await store.PublishAsync(Solution, id, Artifacts(id), Fresh);
        var loaded = await GenerationStore.ResolveAsync(saved.ManifestPath);

        // Assert
        Assert.Equal(id, loaded.Manifest.IndexId);
        Assert.Equal(Path.GetFullPath(Solution), loaded.Manifest.Solution);
        foreach (var name in loaded.Manifest.Artifacts.Keys)
            Assert.Contains(id, await ReadText(loaded, name));
    }

    [Theory]
    [InlineData((int)PublishCheckpoint.BeforeArtifact)]
    [InlineData((int)PublishCheckpoint.AfterArtifact)]
    [InlineData((int)PublishCheckpoint.BeforeInputVerification)]
    [InlineData((int)PublishCheckpoint.BeforeManifestCommit)]
    public async Task FailedPublicationPreservesPreviousSnapshot(int failurePoint)
    {
        // Arrange
        var oldId = Id();
        var previous = await new GenerationStore().PublishAsync(Solution, oldId, Artifacts(oldId), Fresh);
        var manifestBytes = await File.ReadAllBytesAsync(previous.ManifestPath);
        var nextId = Id();
        var failing = new GenerationStore((point, _) => (int)point == failurePoint
            ? Task.FromException(new IOException("Injected disk write failure")) : Task.CompletedTask);

        // Act
        var failure = await Record.ExceptionAsync(() => failing.PublishAsync(Solution, nextId, Artifacts(nextId), Fresh));

        // Assert
        Assert.IsType<IOException>(failure);
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(previous.ManifestPath));
        var current = await GenerationStore.ResolveAsync(previous.ManifestPath);
        Assert.Equal(oldId, current.Manifest.IndexId);
        Assert.Contains(oldId, await ReadText(current, "warnings.txt"));
    }

    [Fact]
    public async Task CancellationImmediatelyBeforeCommitPreservesPreviousSnapshot()
    {
        // Arrange
        var oldId = Id();
        var previous = await new GenerationStore().PublishAsync(Solution, oldId, Artifacts(oldId), Fresh);
        using var cancellation = new CancellationTokenSource();
        var store = new GenerationStore((point, _) =>
        {
            if (point == PublishCheckpoint.BeforeManifestCommit)
                cancellation.Cancel();
            return Task.CompletedTask;
        });
        var nextId = Id();

        // Act
        var failure = await Record.ExceptionAsync(() => store.PublishAsync(Solution, nextId, Artifacts(nextId), Fresh, cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(oldId, (await GenerationStore.ResolveAsync(previous.ManifestPath)).Manifest.IndexId);
    }

    [Fact]
    public async Task SuccessfulCommitReturnsWithoutObservingLaterCancellationOrReportLoss()
    {
        // Arrange
        var oldId = Id();
        var previous = await new GenerationStore().PublishAsync(Solution, oldId, Artifacts(oldId), Fresh);
        var nextId = Id();
        using var cancellation = new CancellationTokenSource();
        var missingReport = Path.Combine(Path.GetDirectoryName(previous.ManifestPath)!, "generations", nextId, "warnings.txt");
        var store = new GenerationStore(commitManifest: (temporary, pointer) =>
        {
            GenerationStore.CommitManifest(temporary, pointer);
            cancellation.Cancel();
            File.Delete(missingReport);
        });

        // Act
        var saved = await store.PublishAsync(Solution, nextId, Artifacts(nextId), Fresh, cancellation.Token);
        var pointer = await File.ReadAllTextAsync(saved.ManifestPath);
        var resolveFailure = await Record.ExceptionAsync(() => GenerationStore.ResolveAsync(saved.ManifestPath));
        var oldWarnings = await ReadText(previous, "warnings.txt");

        // Assert
        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(File.Exists(missingReport));
        Assert.Equal(nextId, saved.Manifest.IndexId);
        Assert.Contains(nextId, pointer);
        Assert.Equal("invalid_index", Assert.IsType<SnapshotStoreException>(resolveFailure).Code);
        Assert.Contains(oldId, oldWarnings);
    }

    [Fact]
    public async Task ChangedInputsPreventCommitAfterAllArtifactsWereWritten()
    {
        // Arrange
        var oldId = Id();
        var previous = await new GenerationStore().PublishAsync(Solution, oldId, Artifacts(oldId), Fresh);
        var nextId = Id();
        Task Reject(CancellationToken _) => Task.FromException(new InvalidOperationException("Tracked input changed"));

        // Act
        var failure = await Record.ExceptionAsync(() => new GenerationStore().PublishAsync(Solution, nextId, Artifacts(nextId), Reject));

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(oldId, (await GenerationStore.ResolveAsync(previous.ManifestPath)).Manifest.IndexId);
    }

    [Fact]
    public async Task ResolvedReaderRemainsOnItsGenerationAfterNewPublication()
    {
        // Arrange
        var store = new GenerationStore();
        var oldId = Id();
        var previous = await store.PublishAsync(Solution, oldId, Artifacts(oldId), Fresh);
        var reader = await GenerationStore.ResolveAsync(previous.ManifestPath);
        var nextId = Id();

        // Act
        await store.PublishAsync(Solution, nextId, Artifacts(nextId), Fresh);
        var latest = await GenerationStore.ResolveAsync(previous.ManifestPath);

        // Assert
        Assert.Equal(nextId, latest.Manifest.IndexId);
        Assert.Equal(oldId, reader.Manifest.IndexId);
        foreach (var name in reader.Manifest.Artifacts.Keys)
            Assert.Contains(oldId, await ReadText(reader, name));
    }

    [Fact]
    public async Task PointerReplacementSucceedsWhileExistingReaderKeepsOldPointerOpen()
    {
        // Arrange
        var store = new GenerationStore();
        var oldId = Id();
        var previous = await store.PublishAsync(Solution, oldId, Artifacts(oldId), Fresh);
        await using var openPointer = new FileStream(previous.ManifestPath, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(openPointer);
        var nextId = Id();

        // Act
        await store.PublishAsync(Solution, nextId, Artifacts(nextId), Fresh);
        var heldPointer = await reader.ReadToEndAsync();
        var current = await GenerationStore.ResolveAsync(previous.ManifestPath);

        // Assert
        Assert.Contains(oldId, heldPointer);
        Assert.DoesNotContain(nextId, heldPointer);
        Assert.Equal(nextId, current.Manifest.IndexId);
    }

    [Fact]
    public async Task TwoSolutionsSharingOneDirectoryHaveIndependentPointersAndReports()
    {
        // Arrange
        var store = new GenerationStore();
        var firstId = Id();
        var secondId = Id();
        var other = Path.Combine(root.FullName, "Another.slnx");

        // Act
        var first = await store.PublishAsync(Solution, firstId, Artifacts(firstId), Fresh);
        var second = await store.PublishAsync(other, secondId, Artifacts(secondId), Fresh);

        // Assert
        Assert.NotEqual(first.ManifestPath, second.ManifestPath);
        Assert.Equal(firstId, (await GenerationStore.ResolveAsync(first.ManifestPath)).Manifest.IndexId);
        Assert.Contains(firstId, await ReadText(first, "warnings.txt"));
        Assert.Contains(secondId, await ReadText(second, "warnings.txt"));
    }

    [Fact]
    public async Task SecondWindowsWriterReportsBusyWithoutChangingActivePointer()
    {
        // Arrange
        if (!OperatingSystem.IsWindows())
            return;
        var oldId = Id();
        var previous = await new GenerationStore().PublishAsync(Solution, oldId, Artifacts(oldId), Fresh);
        using var writerLock = new FileStream(Path.Combine(Path.GetDirectoryName(previous.ManifestPath)!, ".write.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var nextId = Id();

        // Act
        var failure = await Record.ExceptionAsync(() => new GenerationStore().PublishAsync(Solution, nextId, Artifacts(nextId), Fresh));

        // Assert
        Assert.Equal("index_busy", Assert.IsType<SnapshotStoreException>(failure).Code);
        Assert.Equal(oldId, (await GenerationStore.ResolveAsync(previous.ManifestPath)).Manifest.IndexId);
    }

    [Fact]
    public async Task MissingReportFailsResolutionInsteadOfReturningMixedCoverage()
    {
        // Arrange
        var id = Id();
        var saved = await new GenerationStore().PublishAsync(Solution, id, Artifacts(id), Fresh);
        File.Delete(saved.ArtifactPath("warnings.txt"));

        // Act
        var failure = await Record.ExceptionAsync(() => GenerationStore.ResolveAsync(saved.ManifestPath));

        // Assert
        Assert.Equal("invalid_index", Assert.IsType<SnapshotStoreException>(failure).Code);
    }

    [Theory]
    [InlineData("overview.md")]
    [InlineData("warnings.txt")]
    [InlineData("timings.json")]
    public async Task SameLengthReportCorruptionFailsResolution(string name)
    {
        // Arrange
        var id = Id();
        var saved = await new GenerationStore().PublishAsync(Solution, id, Artifacts(id), Fresh);
        var path = saved.ArtifactPath(name);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);

        // Act
        var failure = await Record.ExceptionAsync(() => GenerationStore.ResolveAsync(saved.ManifestPath));

        // Assert
        Assert.Equal("invalid_index", Assert.IsType<SnapshotStoreException>(failure).Code);
    }

    [Theory]
    [InlineData("{", "invalid_index")]
    [InlineData("{\"manifestVersion\":99}", "unsupported_index")]
    [InlineData("{\"manifestVersion\":1,\"indexId\":\"../../elsewhere\"}", "invalid_index")]
    public async Task MalformedUnsupportedAndUnsafeManifestsAreRejected(string json, string code)
    {
        // Arrange
        var path = Path.Combine(root.FullName, "current.json");
        await File.WriteAllTextAsync(path, json);

        // Act
        var failure = await Record.ExceptionAsync(() => GenerationStore.ResolveAsync(path));

        // Assert
        Assert.Equal(code, Assert.IsType<SnapshotStoreException>(failure).Code);
    }

    [Fact]
    public async Task SameSizeCorruptionIsRejectedWhileParserAndHashShareOneReadStream()
    {
        // Arrange
        var id = Id();
        var saved = await new GenerationStore().PublishAsync(Solution, id, Artifacts(id), Fresh);
        var indexPath = saved.ArtifactPath("index.json");
        var original = await File.ReadAllTextAsync(indexPath);
        await File.WriteAllTextAsync(indexPath, original.Replace("snapshot-data", "tampered-data"));
        var decoderCalls = 0;

        // Act
        var failure = await Record.ExceptionAsync(() => GenerationStore.ReadVerifiedAsync(saved, "index.json", async (stream, token) =>
        {
            decoderCalls++;
            return await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: token);
        }));

        // Assert
        Assert.Equal(1, decoderCalls);
        Assert.Equal("invalid_index", Assert.IsType<SnapshotStoreException>(failure).Code);
    }

    [Fact]
    public async Task ReaderAuthenticatesTrailingBytesEvenIfConsumerStopsEarly()
    {
        // Arrange
        var id = Id();
        var saved = await new GenerationStore().PublishAsync(Solution, id, Artifacts(id), Fresh);
        var path = saved.ArtifactPath("overview.md");
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);

        // Act
        var failure = await Record.ExceptionAsync(() => GenerationStore.ReadVerifiedAsync(saved, "overview.md",
            (stream, _) => Task.FromResult(stream.ReadByte())));

        // Assert
        Assert.Equal("invalid_index", Assert.IsType<SnapshotStoreException>(failure).Code);
    }

    [Fact]
    public async Task ConcurrentReadersSeeOnlyEntireOldOrNewGeneration()
    {
        // Arrange
        var store = new GenerationStore();
        var firstId = Id();
        var nextId = Id();
        var first = await store.PublishAsync(Solution, firstId, Artifacts(firstId), Fresh);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = new GenerationStore(async (point, token) =>
        {
            if (point == PublishCheckpoint.BeforeManifestCommit)
            {
                arrived.SetResult();
                await release.Task.WaitAsync(token);
            }
        });
        var publication = delayed.PublishAsync(Solution, nextId, Artifacts(nextId), Fresh);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        var readers = Enumerable.Range(0, 24).Select(async _ =>
        {
            var snapshot = await GenerationStore.ResolveAsync(first.ManifestPath);
            var reports = await Task.WhenAll(snapshot.Manifest.Artifacts.Keys.Select(name => ReadText(snapshot, name)));
            return (snapshot.Manifest.IndexId, Reports: reports);
        }).ToArray();
        release.SetResult();
        var results = await Task.WhenAll(readers);
        await publication;

        // Assert
        Assert.All(results, result =>
        {
            Assert.True(result.IndexId == firstId || result.IndexId == nextId);
            Assert.All(result.Reports, report => Assert.Contains(result.IndexId, report));
        });
    }

    [Fact]
    public async Task ReadsDoNotChangeArtifactOrPointerBytesAndCreateNoFiles()
    {
        // Arrange
        var id = Id();
        var saved = await new GenerationStore().PublishAsync(Solution, id, Artifacts(id), Fresh);
        var before = Directory.GetFiles(root.FullName, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);

        // Act
        var loaded = await GenerationStore.ResolveAsync(saved.ManifestPath);
        foreach (var name in loaded.Manifest.Artifacts.Keys)
            await ReadText(loaded, name);

        // Assert
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(root.FullName, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before)
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public void WindowsCaseAndSeparatorVariantsResolveToSameSolutionStorageIdentity()
    {
        // Arrange
        if (!OperatingSystem.IsWindows())
            return;
        var first = GenerationStore.DefaultPath(Solution);
        var variant = Solution.ToUpperInvariant().Replace('\\', '/');

        // Act
        var second = GenerationStore.DefaultPath(variant);

        // Assert
        Assert.True(StringComparer.OrdinalIgnoreCase.Equals(first, second));
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public async Task AbruptWriterProcessTerminationLeavesAnEntireCommittedGeneration(string killPoint)
    {
        // Arrange
        var oldId = Id();
        var previous = await new GenerationStore().PublishAsync(Solution, oldId, Artifacts(oldId), Fresh);
        var pointerBytes = await File.ReadAllBytesAsync(previous.ManifestPath);
        var nextId = Id();
        var ready = Path.Combine(root.FullName, "child-ready");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(GenerationStoreTests).Assembly.Location);
        start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=CodeAtlas.Tests.GenerationStoreTests.PublicationChildProcessHost");
        start.Environment["CODEATLAS_PUBLICATION_CHILD"] = JsonSerializer.Serialize(new[] { Solution, nextId, ready, killPoint });
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        var expectedId = killPoint == "before" ? oldId : nextId;
        var nextDirectory = Path.Combine(Path.GetDirectoryName(previous.ManifestPath)!, "generations", nextId);
        var recoveryId = Id();

        // Act
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (!File.Exists(ready))
            {
                if (child.HasExited)
                    throw new InvalidOperationException("Publication child exited before checkpoint: " + await stdout + await stderr);
                await Task.Delay(25, timeout.Token);
            }
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            var current = await GenerationStore.ResolveAsync(previous.ManifestPath);
            var reports = await Task.WhenAll(current.Manifest.Artifacts.Keys.Select(name => ReadText(current, name)));
            var pointerAfterKill = await File.ReadAllBytesAsync(previous.ManifestPath);
            var previousWarnings = await ReadText(previous, "warnings.txt");
            // A fresh writer must acquire the lock left by the killed process.
            var recovery = await new GenerationStore().PublishAsync(Solution, recoveryId, Artifacts(recoveryId), Fresh);
            var recovered = await GenerationStore.ResolveAsync(recovery.ManifestPath);

            // Assert
            Assert.Equal(expectedId, current.Manifest.IndexId);
            Assert.All(reports, report => Assert.Contains(expectedId, report));
            if (killPoint == "before")
                Assert.Equal(pointerBytes, pointerAfterKill);
            Assert.True(Directory.Exists(nextDirectory));
            Assert.Contains(oldId, previousWarnings);
            Assert.Equal(recoveryId, recovered.Manifest.IndexId);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
            await Task.WhenAll(stdout, stderr);
        }
    }

    [Fact]
    public async Task PublicationChildProcessHost()
    {
        var encoded = Environment.GetEnvironmentVariable("CODEATLAS_PUBLICATION_CHILD");
        if (encoded is null)
            return;
        var args = JsonSerializer.Deserialize<string[]>(encoded)!;
        async Task Pause()
        {
            await File.WriteAllTextAsync(args[2], "ready");
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        var store = new GenerationStore(async (point, _) =>
        {
            if (args[3] == "before" && point == PublishCheckpoint.BeforeManifestCommit)
                await Pause();
        });
        await store.PublishAsync(args[0], args[1], Artifacts(args[1]), Fresh);
        await Pause();
    }

    public void Dispose() => root.Delete(recursive: true);
}

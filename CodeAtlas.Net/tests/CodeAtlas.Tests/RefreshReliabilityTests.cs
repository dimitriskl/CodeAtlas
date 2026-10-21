using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CodeAtlas.Persistence;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class RefreshReliabilityTests
{
    [Fact]
    public async Task CorruptActiveSnapshotIsAnErrorRatherThanAnUnobservedFullSeed()
    {
        // Arrange
        var fixture = await PhysicalRefreshFixture.CreateAsync();
        var bytes = await File.ReadAllBytesAsync(fixture.Manifest);
        await File.WriteAllTextAsync(fixture.Manifest, "{broken");

        // Act
        var failure = await Record.ExceptionAsync(() => SolutionAnalysis.RefreshIndexAsync(fixture.Solution));

        // Assert
        Assert.Equal("invalid_index", Assert.IsType<IndexFormatException>(failure).Code);
        Assert.Equal("{broken", await File.ReadAllTextAsync(fixture.Manifest));
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public async Task ARealDeniedPointerReadFailsExplicitlyAndRetainsThePreviousGeneration()
    {
        if (!OperatingSystem.IsWindows()) return;
        await VerifyDeniedPointerReadAsync();
    }

    [SupportedOSPlatform("windows")]
    private static async Task VerifyDeniedPointerReadAsync()
    {
        // Arrange
        var fixture = await PhysicalRefreshFixture.CreateAsync();
        var pointer = new FileInfo(fixture.Manifest);
        var bytes = await File.ReadAllBytesAsync(fixture.Manifest);
        var original = pointer.GetAccessControl(AccessControlSections.Access);
        var denied = new FileSecurity();
        denied.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.Read, AccessControlType.Deny));
        try
        {
            pointer.SetAccessControl(denied);

            // Act
            var readFailure = Record.Exception(() => File.ReadAllBytes(fixture.Manifest));
            var failure = await Record.ExceptionAsync(() => SolutionAnalysis.RefreshIndexAsync(fixture.Solution));

            // Assert
            Assert.IsType<UnauthorizedAccessException>(readFailure);
            Assert.Equal("snapshot_io_failed", Assert.IsType<IndexFormatException>(failure).Code);
        }
        finally
        {
            var restored = new FileSecurity();
            restored.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            pointer.SetAccessControl(restored);
        }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Manifest));
    }

    [Fact]
    public async Task VirtualOverlaysEqualFullAnalysisAndCannotEnterProductionStorage()
    {
        // Arrange
        var fixture = await PhysicalRefreshFixture.CreateAsync();
        var diskBefore = await File.ReadAllBytesAsync(fixture.Calculator);
        var pointerBefore = await File.ReadAllBytesAsync(fixture.Manifest);
        var edit = new BenchmarkOverlay(fixture.Calculator, "Calculate(1)", "Calculate(9) + Calculate(10)");
        var expected = await SolutionAnalysis.CreateBenchmarkIndexAsync(fixture.Solution, fixture.Baseline, edit, false);

        // Act
        var actual = await SolutionAnalysis.CreateBenchmarkIndexAsync(fixture.Solution, fixture.Baseline, edit, true);
        var failure = await Record.ExceptionAsync(() => IndexStore.SaveAsync(actual));

        // Assert
        PhysicalRefreshFixture.EqualAnalysis(expected, actual);
        Assert.Equal("incremental-body", actual.Refresh!.Mode);
        Assert.NotNull(actual.VirtualSnapshot);
        Assert.Equal(expected.VirtualSnapshot, actual.VirtualSnapshot);
        Assert.Equal("invalid_index", Assert.IsType<IndexFormatException>(failure).Code);
        Assert.Equal(diskBefore, await File.ReadAllBytesAsync(fixture.Calculator));
        Assert.Equal(pointerBefore, await File.ReadAllBytesAsync(fixture.Manifest));
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public async Task ActualProcessTerminationWithARefreshPayloadHasOnlyOldOrNewOutcomes(string killPoint)
    {
        // Arrange
        var fixture = await PhysicalRefreshFixture.CreateAsync();
        var original = await File.ReadAllTextAsync(fixture.Calculator);
        await File.WriteAllTextAsync(fixture.Calculator, original.Replace("Calculate(1)", "Calculate(5) + Calculate(6)", StringComparison.Ordinal));
        var refreshed = await SolutionAnalysis.RefreshIndexAsync(fixture.Solution);
        var payload = Path.Combine(fixture.Root, "refresh-payload.data");
        await File.WriteAllTextAsync(payload, JsonSerializer.Serialize(refreshed, Json));
        var ready = Path.Combine(fixture.Root, "refresh-child.ready");
        var pointerBefore = await File.ReadAllBytesAsync(fixture.Manifest);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "vstest", typeof(RefreshReliabilityTests).Assembly.Location,
                     "/TestCaseFilter:FullyQualifiedName=CodeAtlas.Tests.RefreshReliabilityTests.RefreshPublicationChildProcessHost" })
            start.ArgumentList.Add(argument);
        start.Environment["CODEATLAS_REFRESH_CHILD"] = JsonSerializer.Serialize(new[] { payload, ready, killPoint });
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        Process? writer = null;
        try
        {
            // Act
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            writer = await PublicationChildProcess.WaitForCheckpointAsync(child, ready, refreshed.IndexId, killPoint, timeout.Token);
            await PublicationChildProcess.KillAndWaitAsync(child, writer, timeout.Token);
            var current = await IndexStore.LoadSnapshotAsync(fixture.Manifest);

            // Assert
            Assert.Equal("incremental-body", refreshed.Refresh!.Mode);
            Assert.Equal(killPoint == "before" ? fixture.Baseline.IndexId : refreshed.IndexId, current.Index.IndexId);
            if (killPoint == "before") Assert.Equal(pointerBefore, await File.ReadAllBytesAsync(fixture.Manifest));
            else PhysicalRefreshFixture.EqualAnalysis(refreshed, current.Index);
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                await PublicationChildProcess.KillAndWaitAsync(child, writer, cleanupTimeout.Token);
                await Task.WhenAll(stdout, stderr).WaitAsync(cleanupTimeout.Token);
            }
            finally { writer?.Dispose(); }
        }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    [Fact]
    public async Task RefreshPublicationChildProcessHost()
    {
        var encoded = Environment.GetEnvironmentVariable("CODEATLAS_REFRESH_CHILD");
        if (encoded is null) return;
        var args = JsonSerializer.Deserialize<string[]>(encoded)!;
        var index = JsonSerializer.Deserialize<CodebaseIndex>(await File.ReadAllTextAsync(args[0]), Json)!;
        async Task Pause()
        {
            await PublicationChildProcess.WriteCheckpointAsync(args[1], index.IndexId, args[2]);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        var store = new GenerationStore(async (point, _) =>
        {
            if (args[2] == "before" && point == PublishCheckpoint.BeforeManifestCommit) await Pause();
        });
        GenerationArtifact Text(string name, string text) => new(name,
            (stream, token) => stream.WriteAsync(Encoding.UTF8.GetBytes(text), token).AsTask());
        await store.PublishAsync(index.Solution, index.IndexId,
        [
            new("index.json", (stream, token) => JsonSerializer.SerializeAsync(stream, index, Json, token)),
            Text("overview.md", IndexStore.Overview(index)),
            Text("warnings.txt", IndexStore.WarningText(index)),
            Text("timings.json", JsonSerializer.Serialize(new { index.IndexId, index.Refresh }, Json))
        ], _ => Task.CompletedTask);
        await Pause();
    }
}

using CodeAtlas.Cli;
using Xunit;

namespace CodeAtlas.Tests;

public class IndexTimingsTests
{
    [Fact]
    public void RepeatedProgressDoesNotResetStageTime()
    {
        // Arrange
        var timings = new IndexTimings();
        timings.Report(IndexStage.ScanningInputs, TimeSpan.FromSeconds(0.1));
        timings.Report(IndexStage.LoadingProjects, TimeSpan.FromSeconds(2));
        timings.Report(IndexStage.LoadingProjects, TimeSpan.FromSeconds(7));
        timings.Report(IndexStage.FindingReferences, TimeSpan.FromSeconds(12));
        timings.Report(IndexStage.FindingReferences, TimeSpan.FromSeconds(18));

        // Act
        var snapshot = timings.Snapshot(TimeSpan.FromSeconds(22));

        // Assert
        Assert.Equal(22, snapshot.ElapsedSeconds);
        Assert.Equal(new[] { new StageTiming("scanningInputs", 2), new("loadingProjects", 10),
            new("findingReferences", 10) }, snapshot.Stages);
        Assert.Equal(snapshot.ElapsedSeconds, snapshot.Stages.Sum(s => s.ElapsedSeconds));
    }

    [Fact]
    public void TakingSnapshotDoesNotEndCurrentStageOrMutatePreviousSnapshot()
    {
        // Arrange
        var timings = new IndexTimings();
        timings.Report(IndexStage.FindingReferences, TimeSpan.Zero);
        var earlier = timings.Snapshot(TimeSpan.FromSeconds(4));
        timings.Report(IndexStage.Saving, TimeSpan.FromSeconds(9));

        // Act
        var later = timings.Snapshot(TimeSpan.FromSeconds(12));

        // Assert
        Assert.Equal(new StageTiming("findingReferences", 4), Assert.Single(earlier.Stages));
        Assert.Equal(new[] { new StageTiming("findingReferences", 9), new("saving", 3) }, later.Stages);
        Assert.Equal(12, later.ElapsedSeconds);
    }

    [Fact]
    public void RepeatedDiscoveryAndSemanticLoadingAccumulateOneStageTotal()
    {
        // Arrange
        var timings = new IndexTimings();
        timings.Report(IndexStage.LoadingProjects, TimeSpan.Zero);
        timings.Report(IndexStage.FingerprintingInputs, TimeSpan.FromSeconds(2));
        timings.Report(IndexStage.LoadingProjects, TimeSpan.FromSeconds(3));
        timings.Report(IndexStage.ValidatingProjects, TimeSpan.FromSeconds(7));

        // Act
        var result = timings.Snapshot(TimeSpan.FromSeconds(8));

        // Assert
        Assert.Equal(new[] { new StageTiming("loadingProjects", 6), new("fingerprintingInputs", 1),
            new("validatingProjects", 1) }, result.Stages);
        Assert.Equal(result.ElapsedSeconds, result.Stages.Sum(stage => stage.ElapsedSeconds));
    }

    [Fact]
    public async Task TimingArtifactBindsSnapshotIdentityAndStatesMeasuredBoundary()
    {
        // Arrange
        using var stream = new MemoryStream();
        var solution = Path.GetFullPath("Sample.slnx");
        var snapshot = new TimingSnapshot(1, [new("saving", 1)]);

        // Act
        await TimingReport.WriteAsync(stream, solution, "new-index", snapshot, CancellationToken.None);
        var report = System.Text.Json.Nodes.JsonNode.Parse(stream.ToArray())!;

        // Assert
        Assert.Equal(solution, report["solution"]!.GetValue<string>());
        Assert.Equal("new-index", report["indexId"]!.GetValue<string>());
        Assert.Equal(CodeAtlasVersion.Current, report["toolVersion"]!.GetValue<string>());
        Assert.Contains("excludes timing staging, final input verification, manifest commit", report["scope"]!.GetValue<string>());
        Assert.Equal(1, report["elapsedSeconds"]!.GetValue<double>());
    }
}

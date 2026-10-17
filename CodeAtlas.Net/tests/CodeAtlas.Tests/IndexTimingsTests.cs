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
    public async Task CancelledReportPreservesPreviousFile()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), $"codeatlas-timings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, ".codeatlas"));
        var path = Path.Combine(directory, ".codeatlas", "timings.json");
        await File.WriteAllTextAsync(path, "previous report");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var snapshot = new TimingSnapshot(1, [new("saving", 1)]);
        try
        {
            // Act
            var error = await Record.ExceptionAsync(() => TimingReport.SaveAsync(
                Path.Combine(directory, "Sample.slnx"), "new-index", snapshot, cancellation.Token));

            // Assert
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.Equal("previous report", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(Path.Combine(directory, ".codeatlas"), "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

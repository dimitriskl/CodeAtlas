using CodeAtlas.Cli;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class IndexProgressReporterTests
{
    [Fact]
    public void ProgressAfterDisposeIsIgnored()
    {
        // Arrange
        using var reporter = new IndexProgressReporter();
        reporter.Dispose();

        // Act
        reporter.Report(new IndexProgress(IndexStage.Saving));

        // Assert
        Assert.Empty(reporter.SnapshotTimings().Stages);
    }
}

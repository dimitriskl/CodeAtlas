using System.Diagnostics;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class PublicationChildProcessTests
{
    [Fact]
    public async Task PublicationFailureKeepsItsOriginalErrorAndCapturedOutputWhenCleanupAlsoFails()
    {
        // Arrange: an unattached OS process makes cleanup fail independently of the first error.
        using var wrapper = new Process();
        var original = new InvalidDataException("first publication failure");
        using var stdout = new HeldOpenReader("vstest stdout evidence");
        using var stderr = new HeldOpenReader("vstest stderr evidence");
        var captured = new ChildProcessOutput(stdout, stderr);

        // Act
        var failure = await Record.ExceptionAsync(() => PublicationChildProcess.FinishAsync(wrapper, null, captured, original));

        // Assert
        Assert.Same(original, failure!.InnerException);
        Assert.Contains("first publication failure", failure.Message);
        Assert.Contains("vstest stdout evidence", failure.Message);
        Assert.Contains("vstest stderr evidence", failure.Message);
        Assert.Contains("Cleanup also failed:", failure.Message);
    }

    [Fact]
    public async Task CheckpointReadSurvivesATransientExclusiveWriterHandle()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("codeatlas-checkpoint-");
        var path = Path.Combine(directory.FullName, "writer.ready");
        using var wrapper = Process.GetCurrentProcess();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await PublicationChildProcess.WriteCheckpointAsync(path, "generation", "before");
        using var held = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(300); held.Dispose(); });
        Process? writer = null;
        try
        {
            // Act
            var failure = await Record.ExceptionAsync(async () => writer = await PublicationChildProcess.WaitForCheckpointAsync(
                wrapper, path, "generation", "before", timeout.Token));

            // Assert
            Assert.Null(failure);
            Assert.Equal(wrapper.Id, writer!.Id);
        }
        finally
        {
            await release;
            writer?.Dispose();
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("testhost")]
    [InlineData("test host (worker) 99")]
    [InlineData("testhost) nested (name)")]
    public void LinuxIdentityReadsTheKernelCounterAfterTheCompleteCommandName(string command)
    {
        // Arrange
        var stat = $"123 ({command}) S " + string.Join(" ", Enumerable.Range(4, 18)) + " 987654321 23 24\n";

        // Act
        var counter = PublicationChildProcess.ParseLinuxStartCounter(stat);

        // Assert
        Assert.Equal(987654321UL, counter);
    }

    [Theory]
    [InlineData("123 invalid")]
    [InlineData("123 (testhost) S 1")]
    [InlineData("123 (testhost) S 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 invalid 23")]
    public void LinuxIdentityRejectsMissingOrMalformedKernelCounters(string stat)
    {
        // Arrange
        var malformed = stat;

        // Act
        var failure = Record.Exception(() => PublicationChildProcess.ParseLinuxStartCounter(malformed));

        // Assert
        Assert.IsType<InvalidDataException>(failure);
    }

    private sealed class HeldOpenReader(string text) : StringReader(text)
    {
        private readonly TaskCompletionSource<int> end = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<int> ReadAsync(char[] buffer, int index, int count)
        {
            var read = Read(buffer, index, count);
            return read > 0 ? Task.FromResult(read) : end.Task;
        }

        protected override void Dispose(bool disposing)
        {
            end.TrySetResult(0);
            base.Dispose(disposing);
        }
    }
}

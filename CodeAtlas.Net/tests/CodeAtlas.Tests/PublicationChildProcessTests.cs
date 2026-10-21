using Xunit;

namespace CodeAtlas.Tests;

public sealed class PublicationChildProcessTests
{
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
}

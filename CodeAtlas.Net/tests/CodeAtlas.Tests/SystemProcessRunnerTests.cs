using CodeAtlas.Cli;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class SystemProcessRunnerTests
{
    [Fact]
    public void RealProcessPreservesInputOutputErrorAndNonzeroExit()
    {
        // Arrange
        var runner = new SystemProcessRunner();
        var command = OperatingSystem.IsWindows() ? "powershell" : "sh";
        string[] arguments = OperatingSystem.IsWindows()
            ? ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write([Console]::In.ReadToEnd()); [Console]::Error.Write('stderr-marker'); exit 7"]
            : ["-c", "cat; printf 'stderr-marker' >&2; exit 7"];

        // Act
        var result = runner.Run(command, arguments, "stdin-marker");

        // Assert
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("stdin-marker", result.StandardOutput);
        Assert.Equal("stderr-marker", result.StandardError);
    }

    [Theory]
    [InlineData("absolute", true)]
    [InlineData("path", true)]
    [InlineData("missing", false)]
    public void ProcessLookupChecksAbsolutePathsAndPathEntries(string source, bool expected)
    {
        // Arrange
        var runner = new SystemProcessRunner();
        var command = source switch
        {
            "absolute" => typeof(SystemProcessRunnerTests).Assembly.Location,
            "path" => OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet",
            _ => "codeatlas-missing-command-" + Guid.NewGuid().ToString("N")
        };

        // Act
        var exists = runner.Exists(command);

        // Assert
        Assert.Equal(expected, exists);
    }

    [Fact]
    public void MissingProcessReportsACredentialStoreFailureWithoutAnUnboundedWait()
    {
        // Arrange
        var runner = new SystemProcessRunner();
        var missing = "codeatlas-missing-command-" + Guid.NewGuid().ToString("N");

        // Act
        var failure = Record.Exception(() => runner.Run(missing, [], null));

        // Assert
        Assert.Contains("could not be started", Assert.IsType<CredentialStoreException>(failure).Message);
    }
}

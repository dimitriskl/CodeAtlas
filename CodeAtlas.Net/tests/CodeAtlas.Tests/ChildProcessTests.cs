using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class ChildProcessTests
{
    [Fact]
    public async Task ChildThatNeverExitsIsKilledWithItsTreeAndFailsWithTheCommandAndOutput()
    {
        // Arrange
        // The child starts a grandchild, prints its id, then waits for it, so neither ever exits on its own.
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell") { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command",
                "$p = Start-Process -FilePath ping -ArgumentList '-n','600','127.0.0.1' -NoNewWindow -PassThru; 'grandchild ' + $p.Id; Wait-Process -Id $p.Id" } }
            : new ProcessStartInfo("sh") { ArgumentList = { "-c", "sleep 600 & echo \"grandchild $!\"; wait" } };

        // Act
        var failure = await Record.ExceptionAsync(() => ChildProcess.RunAsync(start, TimeSpan.FromSeconds(15)));

        // Assert
        var timeout = Assert.IsType<ChildProcessTimeoutException>(failure);
        Assert.Contains(start.FileName + " " + start.ArgumentList[0], timeout.Message);
        var grandchild = Regex.Match(timeout.Message, @"grandchild (\d+)");
        Assert.True(grandchild.Success, timeout.Message);
        Assert.True(await HasExitedAsync(timeout.ProcessId), $"child {timeout.ProcessId} is still running");
        Assert.True(await HasExitedAsync(int.Parse(grandchild.Groups[1].Value)), $"grandchild {grandchild.Groups[1].Value} is still running");
    }

    [Fact]
    public async Task ChildThatExitsWhileADescendantHoldsItsOutputReturnsItsResult()
    {
        // Arrange
        // The child starts a 60 s grandchild that inherits its output pipes, prints its id and exits at once,
        // as a dotnet restore on Windows CI did when a descendant outlived it.
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell") { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command",
                "$p = Start-Process -FilePath ping -ArgumentList '-n','61','127.0.0.1' -NoNewWindow -PassThru; 'grandchild ' + $p.Id" } }
            : new ProcessStartInfo("sh") { ArgumentList = { "-c", "sleep 60 & echo \"grandchild $!\"" } };

        // Act
        var result = await ChildProcess.RunAsync(start, TimeSpan.FromSeconds(40));

        // Assert
        var grandchild = Regex.Match(result.Output, @"grandchild (\d+)");
        if (grandchild.Success) KillIfRunning(int.Parse(grandchild.Groups[1].Value));
        Assert.True(grandchild.Success, result.Output);
        Assert.Equal(0, result.ExitCode);
    }

    private static void KillIfRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
        }
        catch (Exception exited) when (exited is ArgumentException or InvalidOperationException) { }
    }

    private static async Task<bool> HasExitedAsync(int processId)
    {
        // Termination is asynchronous after Kill, so allow the OS a moment to reap the process.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited) return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
            await Task.Delay(100);
        }
        return false;
    }
}

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
    public async Task ChildThatExitsWhileADescendantHoldsItsOutputFailsWithoutClaimingAKill()
    {
        // Arrange
        // The child starts a 10 s grandchild that inherits its output pipes and exits at once.
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd") { ArgumentList = { "/c", "start", "/b", "ping", "-n", "11", "127.0.0.1" } }
            : new ProcessStartInfo("sh") { ArgumentList = { "-c", "sleep 10 &" } };

        // Act
        var failure = await Record.ExceptionAsync(() => ChildProcess.RunAsync(start, TimeSpan.FromSeconds(3)));

        // Assert
        var timeout = Assert.IsType<ChildProcessTimeoutException>(failure);
        Assert.Contains(start.FileName + " " + start.ArgumentList[0], timeout.Message);
        Assert.Contains("output pipes stayed open", timeout.Message);
        Assert.DoesNotContain("killed", timeout.Message);
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

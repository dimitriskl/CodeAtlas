using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace CodeAtlas.Tests;

/// <summary>Runs a test's child process with a bound, so a stuck child fails its test instead of hanging the run.</summary>
internal static class ChildProcess
{
    /// <summary>Longest wait for one child. It stays below the 5-minute blame-hang timeout in qualify-package.py,
    /// so a stuck child fails with its own command and output before the hang dump.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(10);

    internal sealed record Result(int ExitCode, string Output, string Error);

    /// <summary>Starts the child with redirected output and waits for it to exit and close both streams.
    /// On timeout it kills the process tree of a running child and throws <see cref="ChildProcessTimeoutException"/>
    /// with the command, what happened to the child and the output captured so far. A redirected standard input
    /// is closed at once, so the child reads end of input.</summary>
    public static async Task<Result> RunAsync(ProcessStartInfo start, TimeSpan? timeout = null)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {Describe(start)}.");
        if (start.RedirectStandardInput) process.StandardInput.Close();
        var output = new StringBuilder();
        var error = new StringBuilder();
        // Read in chunks rather than ReadToEndAsync, so text written before a hang is still available.
        var streams = Task.WhenAll(PumpAsync(process.StandardOutput, output), PumpAsync(process.StandardError, error));
        using var cancellation = new CancellationTokenSource(timeout ?? DefaultTimeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            // A grandchild that inherited the pipes can keep them open after the child exits.
            await streams.WaitAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // An exited child means a descendant holds the pipes; nothing is killed, so there is nothing to drain.
            var childExited = process.HasExited;
            var outcome = childExited
                ? $"process {process.Id} exited with code {process.ExitCode}, but its output pipes stayed open"
                : Kill(process);
            if (!childExited) await Task.WhenAny(streams, Task.Delay(OutputGrace));
            throw new ChildProcessTimeoutException(
                $"{Describe(start)} did not finish within {timeout ?? DefaultTimeout}; {outcome}."
                + $"{Environment.NewLine}stdout:{Environment.NewLine}{Snapshot(output)}"
                + $"{Environment.NewLine}stderr:{Environment.NewLine}{Snapshot(error)}", process.Id);
        }
        return new(process.ExitCode, Snapshot(output), Snapshot(error));
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder sink)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
            lock (sink) sink.Append(buffer, 0, read);
    }

    /// <summary>Kills the child's process tree and says what happened. A failed kill is reported, not thrown,
    /// so it does not replace the timeout failure and its command and output.</summary>
    private static string Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(OutputGrace);
            return $"killed process tree {process.Id}";
        }
        catch (Exception failure) when (failure is AggregateException or Win32Exception or InvalidOperationException)
        {
            return $"could not kill process tree {process.Id}: {failure.GetType().Name}: {failure.Message}";
        }
    }

    private static string Snapshot(StringBuilder sink)
    {
        lock (sink) return sink.ToString();
    }

    private static string Describe(ProcessStartInfo start) =>
        start.FileName + " " + (start.ArgumentList.Count > 0 ? string.Join(" ", start.ArgumentList) : start.Arguments);
}

/// <summary>A child process, or a descendant holding its output pipes, outlived its bound.</summary>
internal sealed class ChildProcessTimeoutException(string message, int processId) : TimeoutException(message)
{
    /// <summary>The child's process id.</summary>
    public int ProcessId { get; } = processId;
}

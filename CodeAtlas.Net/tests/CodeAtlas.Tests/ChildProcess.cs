using System.Diagnostics;
using System.Text;

namespace CodeAtlas.Tests;

/// <summary>Runs a test's child process with a bound, so a stuck child fails its test instead of hanging the run.</summary>
internal static class ChildProcess
{
    /// <summary>Longest wait for one child. It stays below the 10-minute blame-hang timeout in qualify-package.py,
    /// so a stuck child fails with its own command and output before the hang dump.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(10);

    internal sealed record Result(int ExitCode, string Output, string Error);

    /// <summary>Starts the child with redirected output and waits for it to exit and close both streams.
    /// On timeout it kills the process tree and throws <see cref="ChildProcessTimeoutException"/> with the
    /// command and the output captured so far. A redirected standard input is closed at once, so the child
    /// reads end of input.</summary>
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
            Kill(process);
            await Task.WhenAny(streams, Task.Delay(OutputGrace));
            throw new ChildProcessTimeoutException(
                $"{Describe(start)} did not finish within {timeout ?? DefaultTimeout}; killed process tree {process.Id}."
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

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(OutputGrace);
        }
        catch (InvalidOperationException) when (process.HasExited) { }
    }

    private static string Snapshot(StringBuilder sink)
    {
        lock (sink) return sink.ToString();
    }

    private static string Describe(ProcessStartInfo start) =>
        start.FileName + " " + (start.ArgumentList.Count > 0 ? string.Join(" ", start.ArgumentList) : start.Arguments);
}

/// <summary>A child process outlived its bound and was killed with its descendants.</summary>
internal sealed class ChildProcessTimeoutException(string message, int processId) : TimeoutException(message)
{
    /// <summary>The killed child's process id.</summary>
    public int ProcessId { get; } = processId;
}

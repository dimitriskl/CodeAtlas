using System.ComponentModel;
using System.Diagnostics;

namespace CodeAtlas.Tests;

/// <summary>Runs a test's child process with a bound, so a stuck child fails its test instead of hanging the run.</summary>
internal static class ChildProcess
{
    /// <summary>Longest wait for one child. It stays below the 5-minute blame-hang timeout in qualify-package.py,
    /// so a stuck child fails with its own command and output before the hang dump.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(10);

    internal sealed record Result(int ExitCode, string Output, string Error);

    /// <summary>Starts the child with redirected output, waits for it to exit, then waits a short grace for both
    /// streams to close. On timeout it kills the process tree and throws <see cref="ChildProcessTimeoutException"/>
    /// with the command, the kill outcome and the output captured so far. A redirected standard input receives
    /// <paramref name="input"/>, if any, and is then closed, so the child reads end of input.</summary>
    public static async Task<Result> RunAsync(ProcessStartInfo start, TimeSpan? timeout = null, string? input = null)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {Describe(start)}.");
        if (start.RedirectStandardInput)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }
        var pipes = ChildProcessDiagnostics.PipeNames(process);
        // Read in chunks rather than ReadToEndAsync, so text written before a hang is still available.
        var captured = new ChildProcessOutput(process.StandardOutput, process.StandardError);
        var streams = captured.Completion;
        using var cancellation = new CancellationTokenSource(timeout ?? DefaultTimeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var outcome = Kill(process);
            await Task.WhenAny(streams, Task.Delay(OutputGrace));
            throw new ChildProcessTimeoutException(
                $"{Describe(start)} did not finish within {timeout ?? DefaultTimeout}; {outcome}."
                + $"{Environment.NewLine}stdout:{Environment.NewLine}{captured.Output}"
                + $"{Environment.NewLine}stderr:{Environment.NewLine}{captured.Error}", process.Id);
        }
        // A descendant that inherited the pipes can hold them open long after the child exits (a dotnet restore
        // did on Windows CI). The child's own output is already in the pipes, so only a short drain is needed.
        if (await Task.WhenAny(streams, Task.Delay(OutputGrace)) != streams)
        {
            var diagnostic = $"output drain expired after 10 seconds for {Describe(start)} (pid={process.Id})."
                + Environment.NewLine + await ChildProcessDiagnostics.CaptureAsync(process.Id, pipes);
            Console.Error.WriteLine(diagnostic);
            captured.AppendDiagnostic(diagnostic);
        }
        if (captured.OutputPump.IsFaulted) await captured.OutputPump;
        if (captured.ErrorPump.IsFaulted) await captured.ErrorPump;
        return new(process.ExitCode, captured.Output, captured.Error);
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

    private static string Describe(ProcessStartInfo start) =>
        start.FileName + " " + (start.ArgumentList.Count > 0 ? string.Join(" ", start.ArgumentList) : start.Arguments);
}

/// <summary>A child process outlived its bound; the message says whether its process tree was killed.</summary>
internal sealed class ChildProcessTimeoutException(string message, int processId) : TimeoutException(message)
{
    /// <summary>The child's process id.</summary>
    public int ProcessId { get; } = processId;
}

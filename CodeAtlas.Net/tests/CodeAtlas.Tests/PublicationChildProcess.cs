using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace CodeAtlas.Tests;

/// <summary>Tracks the actual publishing testhost, rather than only its vstest wrapper.</summary>
internal static class PublicationChildProcess
{
    private sealed record Checkpoint(int ProcessId, string ProcessIdentity, string IndexId, string KillPoint);

    public static async Task WriteCheckpointAsync(string path, string indexId, string killPoint)
    {
        using var current = Process.GetCurrentProcess();
        var checkpoint = new Checkpoint(current.Id, ReadProcessIdentity(current), indexId, killPoint);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(checkpoint));
        File.Move(temporary, path);
    }

    public static async Task<Process> WaitForCheckpointAsync(Process wrapper, string path,
        string indexId, string killPoint, CancellationToken cancellationToken)
    {
        while (!File.Exists(path))
        {
            if (wrapper.HasExited)
                throw new InvalidOperationException($"Publication wrapper {wrapper.Id} exited before the {killPoint} checkpoint.");
            await Task.Delay(25, cancellationToken);
        }
        var checkpoint = await ReadCheckpointAsync(path, cancellationToken);
        if (checkpoint is null || checkpoint.ProcessId <= 0 || checkpoint.IndexId != indexId || checkpoint.KillPoint != killPoint)
            throw new InvalidOperationException("Publication checkpoint does not identify the expected writer and generation.");
        var writer = Process.GetProcessById(checkpoint.ProcessId);
        if (writer.HasExited || ReadProcessIdentity(writer) != checkpoint.ProcessIdentity)
        {
            writer.Dispose();
            throw new InvalidOperationException("Publication checkpoint writer has exited or its process identity has changed.");
        }
        return writer;
    }

    private static async Task<Checkpoint?> ReadCheckpointAsync(string path, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                return await JsonSerializer.DeserializeAsync<Checkpoint>(stream, cancellationToken: token);
            }
            catch (Exception failure) when (attempt < 20 && failure is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(10 * attempt, token);
            }
        }
    }

    private static string ReadProcessIdentity(Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            // Process.StartTime adds a boot-to-UTC estimate cached per process.
            // Compare the kernel's original start counter across the two processes.
            var stat = File.ReadAllText($"/proc/{process.Id}/stat");
            return "linux-start:" + ParseLinuxStartCounter(stat).ToString(CultureInfo.InvariantCulture);
        }
        return "start-utc:" + process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
    }

    internal static ulong ParseLinuxStartCounter(string stat)
    {
        // Field 2 (comm) may contain spaces or parentheses. Fields after its
        // final ')' start at field 3; starttime is field 22, hence offset 19.
        var end = stat.LastIndexOf(')');
        if (end < 0) throw new InvalidDataException("Linux process stat has no command terminator.");
        var fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19 || !ulong.TryParse(fields[19], NumberStyles.None,
                CultureInfo.InvariantCulture, out var start))
            throw new InvalidDataException("Linux process stat has no valid start counter.");
        return start;
    }

    public static async Task KillAndWaitAsync(Process wrapper, Process? writer, CancellationToken cancellationToken)
    {
        // Killing a process tree and waiting the wrapper alone does not await descendant exit.
        // Abruptly terminate the identified holder and await both lifetimes before recovery.
        if (writer is not null) KillIfRunning(writer);
        KillIfRunning(wrapper);
        try
        {
            var wrapperExit = wrapper.WaitForExitAsync(cancellationToken);
            if (writer is null) await wrapperExit;
            else await Task.WhenAll(wrapperExit, writer.WaitForExitAsync(cancellationToken));
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Publication writer {writer?.Id.ToString() ?? "unidentified"} "
                + $"or wrapper {wrapper.Id} did not terminate before recovery.", exception);
        }
    }

    public static async Task FinishAsync(Process wrapper, Process? writer, ChildProcessOutput captured,
        Exception? firstFailure)
    {
        Exception? cleanupFailure = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await KillAndWaitAsync(wrapper, writer, timeout.Token);
            await captured.Completion.WaitAsync(timeout.Token);
        }
        catch (Exception failure) { cleanupFailure = failure; }
        finally
        {
            try { writer?.Dispose(); }
            catch (Exception failure) { cleanupFailure ??= failure; }
        }
        if (firstFailure is null && cleanupFailure is null) return;
        var original = firstFailure ?? cleanupFailure!;
        throw new InvalidOperationException($"Publication child failed: {original}"
            + (firstFailure is not null && cleanupFailure is not null ? $"\nCleanup also failed: {cleanupFailure}" : "")
            + $"\nstdout:\n{captured.Output}\nstderr:\n{captured.Error}", original);
    }

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited) { }
    }
}

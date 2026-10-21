using System.Diagnostics;
using System.Text.Json;

namespace CodeAtlas.Tests;

/// <summary>Tracks the actual publishing testhost, rather than only its vstest wrapper.</summary>
internal static class PublicationChildProcess
{
    private sealed record Checkpoint(int ProcessId, long ProcessStartTimeUtcTicks, string IndexId, string KillPoint);

    public static async Task WriteCheckpointAsync(string path, string indexId, string killPoint)
    {
        using var current = Process.GetCurrentProcess();
        var checkpoint = new Checkpoint(current.Id, current.StartTime.ToUniversalTime().Ticks, indexId, killPoint);
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
        var checkpoint = JsonSerializer.Deserialize<Checkpoint>(await File.ReadAllTextAsync(path, cancellationToken));
        if (checkpoint is null || checkpoint.ProcessId <= 0 || checkpoint.IndexId != indexId || checkpoint.KillPoint != killPoint)
            throw new InvalidOperationException("Publication checkpoint does not identify the expected writer and generation.");
        var writer = Process.GetProcessById(checkpoint.ProcessId);
        if (writer.HasExited || writer.StartTime.ToUniversalTime().Ticks != checkpoint.ProcessStartTimeUtcTicks)
        {
            writer.Dispose();
            throw new InvalidOperationException("Publication checkpoint writer has exited or its process identity has changed.");
        }
        return writer;
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

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited) { }
    }
}

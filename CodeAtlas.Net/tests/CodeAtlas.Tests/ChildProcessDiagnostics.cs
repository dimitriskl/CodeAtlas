using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace CodeAtlas.Tests;

/// <summary>Best-effort process evidence after a test child's output drain expires.</summary>
internal static class ChildProcessDiagnostics
{
    private sealed record Entry(int ProcessId, int ParentProcessId, string Name, string? CommandLine);

    internal static string[] PipeNames(Process process)
    {
        if (!OperatingSystem.IsLinux()) return [];
        return new[] { process.StandardOutput.BaseStream, process.StandardError.BaseStream }
            .Select(stream => stream switch
            {
                FileStream file => file.SafeFileHandle.DangerousGetHandle().ToInt64(),
                PipeStream pipe => pipe.SafePipeHandle.DangerousGetHandle().ToInt64(),
                _ => -1
            })
            .Where(handle => handle >= 0)
            .Select(handle => new FileInfo($"/proc/self/fd/{handle}").LinkTarget)
            .OfType<string>().Where(name => name.StartsWith("pipe:[", StringComparison.Ordinal)).Distinct().ToArray();
    }

    internal static async Task<string> CaptureAsync(int childId, string[] pipes)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var entries = OperatingSystem.IsLinux()
                ? await Task.Run(() => LinuxPipeHolders(pipes, timeout.Token), timeout.Token).WaitAsync(timeout.Token)
                : await ProcessTreeAsync(childId, timeout.Token);
            var heading = OperatingSystem.IsLinux() ? "surviving pipe holders" : "surviving descendants";
            return $"{heading} of child {childId}:" + Environment.NewLine
                + (entries.Length == 0 ? "none found (a reparented or inaccessible holder may be unidentifiable)"
                    : string.Join(Environment.NewLine, entries.OrderBy(entry => entry.ProcessId).Select(entry =>
                        $"name={entry.Name} pid={entry.ProcessId} parentPid={entry.ParentProcessId} commandLine={entry.CommandLine ?? "<unavailable>"}")));
        }
        catch (Exception failure) when (failure is OperationCanceledException or IOException or UnauthorizedAccessException
                                           or Win32Exception or JsonException or InvalidOperationException)
        {
            return $"process diagnostics unavailable for child {childId}: {failure.GetType().Name}: {failure.Message}";
        }
    }

    private static Entry[] LinuxPipeHolders(string[] pipes, CancellationToken token)
    {
        var entries = new List<Entry>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            token.ThrowIfCancellationRequested();
            if (!int.TryParse(Path.GetFileName(directory), out var id) || id == Environment.ProcessId) continue;
            try
            {
                var holdsPipe = false;
                foreach (var file in Directory.EnumerateFiles(Path.Combine(directory, "fd")))
                {
                    token.ThrowIfCancellationRequested();
                    if (pipes.Contains(new FileInfo(file).LinkTarget)) { holdsPipe = true; break; }
                }
                if (!holdsPipe) continue;
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                var end = stat.LastIndexOf(')');
                var fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var name = stat[(stat.IndexOf('(') + 1)..end];
                entries.Add(new(id, int.Parse(fields[1]), name,
                    File.ReadAllText(Path.Combine(directory, "cmdline")).Replace('\0', ' ').Trim()));
            }
            catch (Exception unavailable) when (unavailable is IOException or UnauthorizedAccessException) { }
        }
        return entries.ToArray();
    }

    private static async Task<Entry[]> ProcessTreeAsync(int childId, CancellationToken token)
    {
        var windows = OperatingSystem.IsWindows();
        var start = new ProcessStartInfo(windows ? "powershell" : "ps")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        if (windows)
        {
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                "$rows = @(Get-CimInstance Win32_Process); $ids = [Collections.Generic.HashSet[int]]::new(); "
                + $"[void]$ids.Add({childId}); "
                + "do { $added = $false; foreach ($row in $rows) { if ($ids.Contains([int]$row.ParentProcessId) "
                + "-and $ids.Add([int]$row.ProcessId)) { $added = $true } } } while ($added); "
                + $"@($rows | Where-Object {{ $_.ProcessId -ne {childId} -and $ids.Contains([int]$_.ProcessId) }} "
                + "| Select-Object ProcessId,ParentProcessId,Name,CommandLine) | ConvertTo-Json -Compress" })
                start.ArgumentList.Add(argument);
        }
        else foreach (var argument in new[] { "-axo", "pid=,ppid=,comm=,args=" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start process diagnostics.");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException($"Process diagnostics exited {process.ExitCode}: {error}");
            if (windows)
            {
                if (string.IsNullOrWhiteSpace(output)) return [];
                using var json = JsonDocument.Parse(output);
                return json.RootElement.ValueKind == JsonValueKind.Array
                    ? JsonSerializer.Deserialize<Entry[]>(output)! : [JsonSerializer.Deserialize<Entry>(output)!];
            }
            return Descendants(output, childId);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static Entry[] Descendants(string output, int childId)
    {
        var rows = new List<Entry>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 4 && int.TryParse(fields[0], out var id) && int.TryParse(fields[1], out var parent))
                rows.Add(new(id, parent, fields[2], fields[3]));
        }
        var ids = new HashSet<int> { childId };
        while (true)
        {
            var added = rows.Count(row => ids.Contains(row.ParentProcessId) && ids.Add(row.ProcessId));
            if (added == 0) break;
        }
        return rows.Where(row => row.ProcessId != childId && ids.Contains(row.ProcessId)).ToArray();
    }
}

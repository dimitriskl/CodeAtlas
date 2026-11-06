using System.Text;

namespace CodeAtlas.Tests;

/// <summary>Keeps output already received even when a child or inherited pipe never closes.</summary>
internal sealed class ChildProcessOutput
{
    private readonly StringBuilder output = new();
    private readonly StringBuilder error = new();

    internal ChildProcessOutput(TextReader stdout, TextReader stderr)
    {
        OutputPump = PumpAsync(stdout, output);
        ErrorPump = PumpAsync(stderr, error);
        Completion = Task.WhenAll(OutputPump, ErrorPump);
        // Disposal after a bounded drain can fault a still-pending read. Observe that late fault too.
        _ = Completion.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal Task OutputPump { get; }
    internal Task ErrorPump { get; }
    internal Task Completion { get; }
    internal string Output { get { lock (output) return output.ToString(); } }
    internal string Error { get { lock (error) return error.ToString(); } }
    internal void AppendDiagnostic(string text) { lock (error) error.AppendLine(text); }

    private static async Task PumpAsync(TextReader reader, StringBuilder sink)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            lock (sink) sink.Append(buffer, 0, read);
    }
}

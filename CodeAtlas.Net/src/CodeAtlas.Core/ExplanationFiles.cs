using System.Text;
using System.Text.Json;

namespace CodeAtlas.Explanations;

internal static class ExplanationFiles
{
    // A wrapper detects accidental corruption. It does not authenticate a party that can edit both fields.
    private sealed record CheckedDocument(string Sha256, string PayloadJson);

    internal static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken token)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 16_384, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length > maximum) throw new QueryException("explanation_budget_exceeded", "Explanation input exceeds the byte limit.");
        using var result = new MemoryStream();
        var buffer = new byte[16_384];
        int count;
        while ((count = await source.ReadAsync(buffer, token)) != 0)
        {
            if (result.Length + count > maximum) throw new QueryException("explanation_budget_exceeded", "Explanation input grew beyond its byte limit.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    internal static T Parse<T>(byte[] bytes)
    {
        // System.Text.Json alone accepts duplicate names; reject them at every object level first.
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 48 });
        Check(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, ExplanationContract.Json)
            ?? throw ExplanationContract.Invalid("Null explanation document.");

        static void Check(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in element.EnumerateObject())
                {
                    if (!names.Add(p.Name)) throw ExplanationContract.Invalid("Duplicate JSON property: " + p.Name);
                    Check(p.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) Check(child);
        }
    }

    internal static async Task<T> ReadCheckedAsync<T>(string path, int payloadMaximum, CancellationToken token)
    {
        var wrapper = Parse<CheckedDocument>(await ReadBoundedAsync(path, checked(payloadMaximum * 8 + 1024), token));
        if (wrapper.PayloadJson is null || wrapper.Sha256 is null)
            throw ExplanationContract.Invalid("Invalid explanation cache wrapper.");
        var payload = Encoding.UTF8.GetBytes(wrapper.PayloadJson);
        if (payload.Length > payloadMaximum || ExplanationContract.Hash(payload) != wrapper.Sha256)
            throw ExplanationContract.Invalid("Explanation cache checksum mismatch.");
        return Parse<T>(payload);
    }

    internal static Task WriteCheckedAsync<T>(string path, T value, CancellationToken token, bool replace = false)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, ExplanationContract.Json);
        return AtomicWriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(new CheckedDocument(
            ExplanationContract.Hash(payload), Encoding.UTF8.GetString(payload)), ExplanationContract.Json), token, replace);
    }

    internal static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken token, bool replace = false)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16_384, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, token);
                await stream.FlushAsync(token);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            if (replace && File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path); // First writer wins; no non-atomic overwrite fallback.
            // No cancellable ancillary work after commit.
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static string InsideRoot(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Contains('\\') || relative.Split('/').Any(p => p is "" or "." or ".." || p.Contains(':')))
            throw ExplanationContract.Invalid("Only canonical solution-relative paths are supported.");
        root = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            throw ExplanationContract.Invalid("Context path escapes the solution root.");
        // The qualified M4 model excludes alias paths. Reject reparse components rather than reading through one.
        for (var current = new FileInfo(full) as FileSystemInfo; current is not null; current = current switch
        {
            FileInfo f => f.Directory,
            DirectoryInfo d => d.Parent,
            _ => null
        })
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw ExplanationContract.Invalid("Reparse/alias context paths are not supported.");
        }
        return full;
    }
}

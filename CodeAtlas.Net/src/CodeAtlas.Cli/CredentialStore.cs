using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAtlas.Cli;

/// <summary>A credential failure. Messages never contain the secret or process output.</summary>
internal sealed class CredentialStoreException(string message) : Exception(message);

internal sealed record CredentialLookup(string Value, string Location);

internal sealed record CredentialSaveResult(string Location, string? Warning);

/// <summary>Stores provider API keys outside the repository: an OS keychain first, then an owner-only file.</summary>
internal interface ICredentialStore
{
    CredentialLookup? Find(string provider);
    CredentialSaveResult Save(string provider, string secret);
    IReadOnlyList<string> Remove(string provider);
}

/// <summary>One place a key can be kept (OS keychain or fallback file).</summary>
internal interface ICredentialBackend
{
    string Name { get; }
    bool IsAvailable { get; }
    string? Read(string provider);
    void Write(string provider, string secret);
    bool Delete(string provider);
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs keychain command-line tools. Secrets are passed on standard input, never as arguments.</summary>
internal interface IProcessRunner
{
    bool Exists(string command);
    ProcessResult Run(string command, IReadOnlyList<string> arguments, string? standardInput);
}

internal sealed class CredentialStore(IReadOnlyList<ICredentialBackend> keychains, ICredentialBackend file) : ICredentialStore
{
    internal const string FileWarning = "This file is not encrypted; only its owner-only permissions protect the key.";

    public CredentialLookup? Find(string provider)
    {
        foreach (var backend in keychains.Where(backend => backend.IsAvailable).Append(file))
        {
            string? value;
            try { value = backend.Read(provider); }
            catch (CredentialStoreException) { continue; }
            if (!string.IsNullOrWhiteSpace(value)) return new(value, backend.Name);
        }
        return null;
    }

    public CredentialSaveResult Save(string provider, string secret)
    {
        foreach (var backend in keychains.Where(backend => backend.IsAvailable))
        {
            try
            {
                backend.Write(provider, secret);
                // A newer keychain entry replaces an older fallback copy, so only one place holds the key.
                try { file.Delete(provider); } catch (CredentialStoreException) { }
                return new(backend.Name, null);
            }
            catch (CredentialStoreException) { }
        }
        file.Write(provider, secret);
        return new(file.Name, FileWarning);
    }

    public IReadOnlyList<string> Remove(string provider)
    {
        var removed = new List<string>();
        foreach (var backend in keychains.Where(backend => backend.IsAvailable).Append(file))
        {
            try { if (backend.Read(provider) is not null && backend.Delete(provider)) removed.Add(backend.Name); }
            catch (CredentialStoreException) { }
        }
        return removed;
    }

    internal static CredentialStore ForCurrentUser()
    {
        var runner = new SystemProcessRunner();
        return new([
            new WindowsCredentialManagerBackend(),
            new MacKeychainBackend(runner, OperatingSystem.IsMacOS()),
            new LinuxSecretServiceBackend(runner, OperatingSystem.IsLinux(), Environment.GetEnvironmentVariable)
        ], new CredentialFileBackend(CredentialFileBackend.DefaultPath()));
    }
}

/// <summary>Windows Credential Manager through advapi32 (generic credentials, current user, this machine).</summary>
internal sealed class WindowsCredentialManagerBackend(string targetPrefix = "CodeAtlas:ai:") : ICredentialBackend
{
    private const uint GenericType = 1;
    private const uint PersistLocalMachine = 2;
    private const int NotFound = 1168;
    private const int MaximumBlobBytes = 2560;

    public string Name => "Windows Credential Manager";
    public bool IsAvailable => OperatingSystem.IsWindows();

    public string? Read(string provider)
    {
        if (!IsAvailable) return null;
        if (!CredRead(Target(provider), GenericType, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            return error == NotFound ? null : throw new CredentialStoreException($"Windows Credential Manager read failed (error {error}).");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(handle);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(handle); }
    }

    public void Write(string provider, string secret)
    {
        if (!IsAvailable) throw new CredentialStoreException("Windows Credential Manager is not available.");
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length > MaximumBlobBytes) throw new CredentialStoreException("The key is too long for Windows Credential Manager.");
        var target = Marshal.StringToCoTaskMemUni(Target(provider));
        var user = Marshal.StringToCoTaskMemUni("codeatlas");
        var comment = Marshal.StringToCoTaskMemUni("CodeAtlas AI provider key");
        var blob = Marshal.AllocCoTaskMem(Math.Max(1, bytes.Length));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = GenericType, TargetName = target, Comment = comment, UserName = user,
                CredentialBlobSize = (uint)bytes.Length, CredentialBlob = blob, Persist = PersistLocalMachine
            };
            if (!CredWrite(ref credential, 0))
                throw new CredentialStoreException($"Windows Credential Manager write failed (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Array.Clear(bytes);
            Marshal.FreeCoTaskMem(blob);
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(user);
            Marshal.FreeCoTaskMem(comment);
        }
    }

    public bool Delete(string provider)
    {
        if (!IsAvailable) return false;
        if (CredDelete(Target(provider), GenericType, 0)) return true;
        var error = Marshal.GetLastWin32Error();
        return error == NotFound ? false : throw new CredentialStoreException($"Windows Credential Manager delete failed (error {error}).");
    }

    private string Target(string provider) => targetPrefix + provider;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}

/// <summary>macOS Keychain through the system <c>security</c> tool.</summary>
internal sealed class MacKeychainBackend(IProcessRunner runner, bool isMacOS, string service = "CodeAtlas") : ICredentialBackend
{
    internal const string Tool = "/usr/bin/security";
    private const int NotFound = 44;

    public string Name => "macOS Keychain";
    public bool IsAvailable => isMacOS && runner.Exists(Tool);

    public string? Read(string provider)
    {
        var result = runner.Run(Tool, ["find-generic-password", "-s", service, "-a", provider, "-w"], null);
        if (result.ExitCode == NotFound) return null;
        if (result.ExitCode != 0) throw new CredentialStoreException($"macOS Keychain read failed (exit {result.ExitCode}).");
        var value = result.StandardOutput.TrimEnd('\r', '\n');
        return value.Length == 0 ? null : value;
    }

    public void Write(string provider, string secret)
    {
        // "security -i" reads the command from standard input, so the key never appears in a process argument list.
        if (secret.Any(c => c is '"' or '\\' || char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new CredentialStoreException("The key contains characters the macOS Keychain tool cannot accept on standard input.");
        var command = $"add-generic-password -U -s \"{service}\" -a \"{provider}\" -l \"CodeAtlas AI key ({provider})\" -w \"{secret}\"\n";
        var result = runner.Run(Tool, ["-i"], command);
        if (result.ExitCode != 0 || Read(provider) != secret)
            throw new CredentialStoreException($"macOS Keychain write failed (exit {result.ExitCode}).");
    }

    public bool Delete(string provider)
    {
        var result = runner.Run(Tool, ["delete-generic-password", "-s", service, "-a", provider], null);
        return result.ExitCode == 0;
    }
}

/// <summary>Linux Secret Service (GNOME Keyring, KWallet) through <c>secret-tool</c>, when installed and a session bus exists.</summary>
internal sealed class LinuxSecretServiceBackend(IProcessRunner runner, bool isLinux, Func<string, string?> readEnvironment,
    string service = "codeatlas") : ICredentialBackend
{
    internal const string Tool = "secret-tool";

    public string Name => "Linux Secret Service";
    public bool IsAvailable => isLinux && !string.IsNullOrWhiteSpace(readEnvironment("DBUS_SESSION_BUS_ADDRESS")) && runner.Exists(Tool);

    public string? Read(string provider)
    {
        var result = runner.Run(Tool, ["lookup", "service", service, "provider", provider], null);
        if (result.ExitCode != 0) return null;
        var value = result.StandardOutput.TrimEnd('\r', '\n');
        return value.Length == 0 ? null : value;
    }

    public void Write(string provider, string secret)
    {
        // secret-tool reads the secret from standard input when it is not a terminal.
        var result = runner.Run(Tool, ["store", $"--label=CodeAtlas AI key ({provider})", "service", service, "provider", provider], secret);
        if (result.ExitCode != 0 || Read(provider) != secret)
            throw new CredentialStoreException($"Linux Secret Service write failed (exit {result.ExitCode}).");
    }

    public bool Delete(string provider) =>
        runner.Run(Tool, ["clear", "service", service, "provider", provider], null).ExitCode == 0;
}

/// <summary>Fallback for systems without a keychain. Not encrypted: protected by owner-only permissions.</summary>
internal sealed class CredentialFileBackend(string path) : ICredentialBackend
{
    private const string Prefix = "b64:";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal string Path { get; } = System.IO.Path.GetFullPath(path);
    public string Name => "credentials file " + Path;
    public bool IsAvailable => true;

    internal static string DefaultPath()
    {
        if (OperatingSystem.IsWindows())
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodeAtlas", "credentials.json");
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config) || !System.IO.Path.IsPathRooted(config))
            config = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return System.IO.Path.Combine(config, "codeatlas", "credentials.json");
    }

    public string? Read(string provider)
    {
        var keys = Load();
        try
        {
            if (keys[provider]?.GetValue<string>() is not { } stored) return null;
            return stored.StartsWith(Prefix, StringComparison.Ordinal) ? Encoding.UTF8.GetString(Convert.FromBase64String(stored[Prefix.Length..])) : stored;
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException) { throw Invalid(); }
    }

    public void Write(string provider, string secret)
    {
        var keys = Load();
        keys[provider] = Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
        Save(keys);
    }

    public bool Delete(string provider)
    {
        var keys = Load();
        if (!keys.Remove(provider)) return false;
        if (keys.Count == 0)
        {
            try { File.Delete(Path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { throw new CredentialStoreException($"The credentials file {Path} could not be deleted."); }
        }
        else Save(keys);
        return true;
    }

    /// <summary>True when only the current user can read or write the file.</summary>
    internal static bool HasOwnerOnlyAccess(string file)
    {
        if (OperatingSystem.IsWindows()) return WindowsOwnerOnly(file);
        return File.GetUnixFileMode(file) == (UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private JsonObject Load()
    {
        if (!File.Exists(Path)) return new();
        try
        {
            if (new FileInfo(Path).Length > 65_536) throw Invalid();
            var root = JsonNode.Parse(File.ReadAllText(Path)) as JsonObject ?? throw Invalid();
            return root["keys"] is JsonObject keys ? (JsonObject)keys.DeepClone() : new();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { throw Invalid(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new CredentialStoreException($"The credentials file {Path} could not be read."); }
    }

    private void Save(JsonObject keys)
    {
        var root = new JsonObject
        {
            ["version"] = 1,
            ["notice"] = "CodeAtlas AI keys. Not encrypted: protected only by owner-only file permissions. Never commit or share this file.",
            ["keys"] = keys
        };
        var content = Encoding.UTF8.GetBytes(root.ToJsonString(Json) + Environment.NewLine);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
                using (var stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                           FileShare.None, 4096, FileOptions.None, WindowsOwnerOnlySecurity()))
                    stream.Write(content);
            }
            else
            {
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                };
                using (var stream = new FileStream(temporary, options)) stream.Write(content);
            }
            File.Move(temporary, Path, overwrite: true);
            if (OperatingSystem.IsWindows()) new FileInfo(Path).SetAccessControl(WindowsOwnerOnlySecurity());
            else File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        { throw new CredentialStoreException($"The credentials file {Path} could not be written."); }
        finally
        {
            Array.Clear(content);
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity WindowsOwnerOnlySecurity()
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new CredentialStoreException("The current Windows user could not be identified.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static bool WindowsOwnerOnly(string file)
    {
        var user = WindowsIdentity.GetCurrent().User;
        var security = new FileInfo(file).GetAccessControl();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToArray();
        return security.AreAccessRulesProtected && rules.Length > 0
            && rules.All(rule => rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Equals(user));
    }

    private CredentialStoreException Invalid() => new($"The credentials file {Path} is invalid or larger than 64 KiB; it was not printed or replaced.");
}

internal sealed class SystemProcessRunner : IProcessRunner
{
    public bool Exists(string command)
    {
        if (System.IO.Path.IsPathRooted(command)) return File.Exists(command);
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(System.IO.Path.Combine(directory, command)));
    }

    public ProcessResult Run(string command, IReadOnlyList<string> arguments, string? standardInput)
    {
        var start = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process? process;
        try { process = Process.Start(start); }
        catch (Win32Exception) { throw new CredentialStoreException($"{System.IO.Path.GetFileName(command)} could not be started."); }
        using (process)
        {
            if (process is null) throw new CredentialStoreException($"{System.IO.Path.GetFileName(command)} could not be started.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                if (standardInput is not null) process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            }
            catch (IOException) { }
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new CredentialStoreException($"{System.IO.Path.GetFileName(command)} did not finish within 30 seconds.");
            }
            return new(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
    }
}

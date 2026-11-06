using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace CodeAtlas;

public sealed record SqliteStatus(bool Available, string? Version, bool Fts5, string? Failure)
{
    /// <summary>"bundled" (e_sqlite3 shipped with CodeAtlas) or "system" (fallback); null when nothing loaded.</summary>
    public string? Library { get; init; }
    public string[] Instructions { get; init; } = [];
}

/// <summary>The SQLite store is unusable on this machine. Never installs anything by itself.</summary>
public sealed class SqliteUnavailableException(string message, IReadOnlyList<string> instructions)
    : AnalysisException(message, instructions)
{
    public string Code => "sqlite_unavailable";
}

/// <summary>
/// Checks the bundled native SQLite library once per process: open an in-memory database,
/// read its version and require FTS5. Tests inject a failure through <see cref="Probe"/>.
/// </summary>
public static class SqliteAvailability
{
    public const string MinimumVersion = "3.35.0";
    private static readonly object Gate = new();
    private static SqliteStatus? cached;
    private static Func<SqliteStatus>? probe;

    /// <summary>Test seam: replaces the real probe and clears the cached result. Pass null to restore.</summary>
    public static Func<SqliteStatus>? Probe
    {
        get => probe;
        set { lock (Gate) { probe = value; cached = null; } }
    }

    /// <summary>Set to a reason to simulate an unusable library in a child process (contract tests).</summary>
    public const string SimulateVariable = "CODEATLAS_SIMULATE_SQLITE_UNAVAILABLE";

    public static SqliteStatus Check()
    {
        lock (Gate)
            return cached ??= probe?.Invoke() ?? Simulated() ?? Run();
    }

    private static SqliteStatus? Simulated() =>
        Environment.GetEnvironmentVariable(SimulateVariable) is { Length: > 0 } reason
            ? Failed(null, false, "Simulated: " + reason)
            : null;

    public static void Require()
    {
        var status = Check();
        if (!status.Available)
            throw new SqliteUnavailableException(
                "SQLite is unavailable, so the CodeAtlas index store cannot be opened. " + status.Failure, InstructionsFor(status));
    }

    private static SqliteStatus Run()
    {
        RegisterSystemFallback();
        try
        {
            using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sqlite_version()";
            var version = (string)command.ExecuteScalar()!;
            if (!Version.TryParse(version, out var parsed) || parsed < Version.Parse(MinimumVersion))
                return Failed(version, false, $"SQLite {version} is older than the required {MinimumVersion}.");
            command.CommandText = "SELECT sqlite_compileoption_used('ENABLE_FTS5')";
            var fts5 = Convert.ToInt64(command.ExecuteScalar()) == 1;
            if (!fts5)
            {
                // Some builds register FTS5 without the compile-option flag; creating a table is decisive.
                try
                {
                    command.CommandText = "CREATE VIRTUAL TABLE temp.probe USING fts5(x)";
                    command.ExecuteNonQuery();
                    fts5 = true;
                }
                catch (SqliteException) { }
            }
            return fts5 ? new(true, version, true, null) { Library = loadedLibrary }
                : Failed(version, false, $"SQLite {version} was loaded without the FTS5 full-text module.");
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException
            or EntryPointNotFoundException or TypeInitializationException or SqliteException or InvalidOperationException)
        {
            return Failed(null, false, "The native SQLite library could not be loaded: " + Innermost(exception).Message);
        }
    }

    private static bool resolverRegistered;
    private static string? loadedLibrary;

    // The provider imports "e_sqlite3". When the bundled native file cannot load (unsupported
    // platform, blocked file), resolve the same C API from the operating system's SQLite.
    private static void RegisterSystemFallback()
    {
        if (resolverRegistered) return;
        resolverRegistered = true;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly, (name, assembly, searchPath) =>
            {
                if (name != "e_sqlite3") return IntPtr.Zero;
                if (NativeLibrary.TryLoad(name, assembly, searchPath, out var bundled))
                {
                    loadedLibrary = "bundled";
                    return bundled;
                }
                string[] candidates = OperatingSystem.IsWindows() ? ["winsqlite3", "sqlite3"]
                    : OperatingSystem.IsMacOS() ? ["libsqlite3.dylib", "/usr/lib/libsqlite3.dylib"]
                    : ["libsqlite3.so.0", "libsqlite3.so"];
                foreach (var candidate in candidates)
                    if (NativeLibrary.TryLoad(candidate, out var system))
                    {
                        loadedLibrary = "system";
                        return system;
                    }
                return IntPtr.Zero;
            });
        }
        catch (InvalidOperationException)
        {
            // A resolver is already registered by the host; keep its behavior.
        }
    }

    private static SqliteStatus Failed(string? version, bool fts5, string failure)
    {
        var status = new SqliteStatus(false, version, fts5, failure);
        return status with { Instructions = InstructionsFor(status) };
    }

    private static Exception Innermost(Exception exception)
    {
        while (exception.InnerException is not null) exception = exception.InnerException;
        return exception;
    }

    /// <summary>Exact, platform-specific guidance. CodeAtlas never runs these commands silently.</summary>
    public static string[] InstructionsFor(SqliteStatus status)
    {
        var architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var common = new[]
        {
            "CodeAtlas ships the SQLite native library (SQLitePCLRaw.bundle_e_sqlite3) for Windows, Linux (x64/arm64) and macOS.",
            "Reinstall or update the tool first: dotnet tool update --global CodeAtlas.Tool",
            $"Detected platform: {RuntimeInformation.OSDescription} ({architecture}), runtime {RuntimeInformation.RuntimeIdentifier}."
        };
        string[] platform;
        if (OperatingSystem.IsWindows())
            platform =
            [
                "Windows: check that antivirus or application control does not block e_sqlite3.dll in the tool folder.",
                "If the tool folder is damaged, uninstall and reinstall: dotnet tool uninstall --global CodeAtlas.Tool && dotnet tool install --global CodeAtlas.Tool"
            ];
        else if (OperatingSystem.IsMacOS())
            platform =
            [
                "macOS: allow libe_sqlite3.dylib if Gatekeeper quarantined it: xattr -dr com.apple.quarantine ~/.dotnet/tools/.store/codeatlas.tool",
                "System SQLite with FTS5: brew install sqlite"
            ];
        else if (OperatingSystem.IsLinux())
            platform =
            [
                "Debian/Ubuntu: sudo apt install libsqlite3-0",
                "Fedora/RHEL: sudo dnf install sqlite-libs",
                "Alpine: sudo apk add sqlite-libs",
                "Unsupported CPU architectures need a system SQLite (3.35 or later) built with FTS5."
            ];
        else
            platform = ["Install a system SQLite 3.35 or later built with FTS5 for this platform."];
        return common.Concat(platform).ToArray();
    }
}

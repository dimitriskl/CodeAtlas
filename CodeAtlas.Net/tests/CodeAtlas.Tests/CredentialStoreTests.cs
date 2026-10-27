using System.Text.RegularExpressions;
using CodeAtlas.Cli;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class CredentialStoreTests : IDisposable
{
    private const string Secret = "sk-synthetic-credential-0123456789";
    private readonly string root = Path.Combine(Path.GetTempPath(), "codeatlas-credentials-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(root, "config", "codeatlas", "credentials.json");

    [Fact]
    public void MacKeychainReceivesTheSecretOnStandardInputOnly()
    {
        // Arrange
        var keychain = new Dictionary<string, string>();
        var runner = new FakeProcessRunner(call => MacKeychain(call, keychain), MacKeychainBackend.Tool);
        var backend = new MacKeychainBackend(runner, isMacOS: true);

        // Act
        backend.Write("openai", Secret);
        var read = backend.Read("openai");
        var deleted = backend.Delete("openai");

        // Assert
        Assert.Equal(Secret, read);
        Assert.True(deleted);
        Assert.Empty(keychain);
        Assert.All(runner.Calls, call => Assert.DoesNotContain(call.Arguments, argument => argument.Contains(Secret)));
        var write = Assert.Single(runner.Calls, call => call.Arguments.SequenceEqual(["-i"]));
        Assert.Contains(Secret, write.StandardInput);
        Assert.Equal("macOS Keychain", backend.Name);
    }

    [Fact]
    public void MacKeychainMissingEntryIsNullAndUnavailableOffMacOrWithoutTool()
    {
        // Arrange
        var runner = new FakeProcessRunner(_ => new(44, "", "The specified item could not be found."), MacKeychainBackend.Tool);

        // Act
        var missing = new MacKeychainBackend(runner, isMacOS: true).Read("kimi");
        var offMac = new MacKeychainBackend(runner, isMacOS: false).IsAvailable;
        var withoutTool = new MacKeychainBackend(new FakeProcessRunner(_ => new(0, "", "")), isMacOS: true).IsAvailable;

        // Assert
        Assert.Null(missing);
        Assert.False(offMac);
        Assert.False(withoutTool);
    }

    [Fact]
    public void MacKeychainRejectsKeysThatCannotBeQuotedWithoutRunningTheTool()
    {
        // Arrange
        var runner = new FakeProcessRunner(_ => new(0, "", ""), MacKeychainBackend.Tool);
        var backend = new MacKeychainBackend(runner, isMacOS: true);

        // Act
        var error = Record.Exception(() => backend.Write("openai", "sk-\"quoted\""));

        // Assert
        Assert.IsType<CredentialStoreException>(error);
        Assert.DoesNotContain("quoted", error.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void LinuxSecretServiceStoresFromStandardInputAndReadsBack()
    {
        // Arrange
        var secrets = new Dictionary<string, string>();
        var runner = new FakeProcessRunner(call => SecretTool(call, secrets), LinuxSecretServiceBackend.Tool);
        var backend = new LinuxSecretServiceBackend(runner, isLinux: true, name => name == "DBUS_SESSION_BUS_ADDRESS" ? "unix:path=/run/bus" : null);

        // Act
        var available = backend.IsAvailable;
        backend.Write("openrouter", Secret);
        var read = backend.Read("openrouter");
        var deleted = backend.Delete("openrouter");

        // Assert
        Assert.True(available);
        Assert.Equal(Secret, read);
        Assert.True(deleted);
        Assert.Null(backend.Read("openrouter"));
        Assert.All(runner.Calls, call => Assert.DoesNotContain(call.Arguments, argument => argument.Contains(Secret)));
        var store = Assert.Single(runner.Calls, call => call.Arguments[0] == "store");
        Assert.Equal(Secret, store.StandardInput);
        Assert.Equal("Linux Secret Service", backend.Name);
    }

    [Fact]
    public void LinuxSecretServiceNeedsLinuxASessionBusAndSecretTool()
    {
        // Arrange
        var withTool = new FakeProcessRunner(_ => new(0, "", ""), LinuxSecretServiceBackend.Tool);
        var withoutTool = new FakeProcessRunner(_ => new(0, "", ""));
        Func<string, string?> bus = name => name == "DBUS_SESSION_BUS_ADDRESS" ? "unix:path=/run/bus" : null;

        // Act
        var headless = new LinuxSecretServiceBackend(withTool, isLinux: true, _ => null).IsAvailable;
        var missingTool = new LinuxSecretServiceBackend(withoutTool, isLinux: true, bus).IsAvailable;
        var notLinux = new LinuxSecretServiceBackend(withTool, isLinux: false, bus).IsAvailable;

        // Assert
        Assert.False(headless);
        Assert.False(missingTool);
        Assert.False(notLinux);
    }

    [Fact]
    public void LinuxSecretServiceWriteFailureIsReportedWithoutTheSecret()
    {
        // Arrange
        var runner = new FakeProcessRunner(_ => new(1, "", "Cannot autolaunch D-Bus without X11"), LinuxSecretServiceBackend.Tool);
        var backend = new LinuxSecretServiceBackend(runner, isLinux: true, _ => "unix:path=/run/bus");

        // Act
        var error = Record.Exception(() => backend.Write("openai", Secret));

        // Assert
        Assert.IsType<CredentialStoreException>(error);
        Assert.DoesNotContain(Secret, error.Message);
    }

    [Fact]
    public void FallbackFileRoundTripsWithOwnerOnlyPermissionsAndNoPlainSecret()
    {
        // Arrange
        var backend = new CredentialFileBackend(FilePath);

        // Act
        backend.Write("openai", Secret);
        backend.Write("anthropic", Secret + "-second");
        var read = backend.Read("openai");
        var text = File.ReadAllText(FilePath);

        // Assert
        Assert.Equal(Secret, read);
        Assert.Equal(Secret + "-second", backend.Read("anthropic"));
        Assert.True(CredentialFileBackend.HasOwnerOnlyAccess(FilePath));
        Assert.DoesNotContain(Secret, text);
        Assert.Contains("Not encrypted", text);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(FilePath)!, "*.tmp"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(FilePath)!));
    }

    [Fact]
    public void FallbackFileTightensBroaderPermissionsOnTheNextSave()
    {
        // Arrange
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{\"version\":1,\"keys\":{}}");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var broadBefore = !CredentialFileBackend.HasOwnerOnlyAccess(FilePath);

        // Act
        new CredentialFileBackend(FilePath).Write("kimi", Secret);

        // Assert
        Assert.True(broadBefore);
        Assert.True(CredentialFileBackend.HasOwnerOnlyAccess(FilePath));
    }

    [Fact]
    public void FallbackFileDeletesItselfWhenTheLastKeyIsRemoved()
    {
        // Arrange
        var backend = new CredentialFileBackend(FilePath);
        backend.Write("openai", Secret);

        // Act
        var deleted = backend.Delete("openai");
        var again = backend.Delete("openai");

        // Assert
        Assert.True(deleted);
        Assert.False(again);
        Assert.False(File.Exists(FilePath));
        Assert.Null(backend.Read("openai"));
    }

    [Fact]
    public void CorruptFallbackFileFailsWithoutPrintingOrReplacingIt()
    {
        // Arrange
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        const string original = "{secret-canary";
        File.WriteAllText(FilePath, original);
        var backend = new CredentialFileBackend(FilePath);

        // Act
        var error = Record.Exception(() => backend.Write("openai", Secret));

        // Assert
        Assert.IsType<CredentialStoreException>(error);
        Assert.DoesNotContain("secret-canary", error.Message);
        Assert.Equal(original, File.ReadAllText(FilePath));
    }

    [Fact]
    public void FallbackFileDefaultLocationIsOutsideTheRepository()
    {
        // Act
        var path = CredentialFileBackend.DefaultPath();

        // Assert
        Assert.EndsWith("credentials.json", path);
        Assert.DoesNotContain(".codeatlas", path);
        Assert.Contains(OperatingSystem.IsWindows() ? Path.Combine("CodeAtlas", "credentials.json") : Path.Combine("codeatlas", "credentials.json"), path);
    }

    [Fact]
    public void StoreSavesInTheKeychainAndRemovesAnOlderFileCopy()
    {
        // Arrange
        var keychain = new FakeCredentialBackend("Test Keychain");
        var file = new FakeCredentialBackend("credentials file");
        file.Keys["openai"] = "synthetic-old";
        var store = new CredentialStore([keychain], file);

        // Act
        var saved = store.Save("openai", Secret);
        var found = store.Find("openai");

        // Assert
        Assert.Equal(new CredentialSaveResult("Test Keychain", null), saved);
        Assert.Equal(new CredentialLookup(Secret, "Test Keychain"), found);
        Assert.Empty(file.Keys);
    }

    [Fact]
    public void StoreFallsBackToTheFileWithAWarningWhenNoKeychainWorks()
    {
        // Arrange
        var broken = new FakeCredentialBackend("Broken Keychain", failWrite: true);
        var missing = new FakeCredentialBackend("Missing Keychain", available: false);
        var file = new FakeCredentialBackend("credentials file");
        var store = new CredentialStore([broken, missing], file);

        // Act
        var saved = store.Save("azure", Secret);

        // Assert
        Assert.Equal("credentials file", saved.Location);
        Assert.Equal(CredentialStore.FileWarning, saved.Warning);
        Assert.Contains("not encrypted", saved.Warning);
        Assert.Equal(Secret, file.Keys["azure"]);
        Assert.Empty(missing.Keys);
        Assert.Equal(new CredentialLookup(Secret, "credentials file"), store.Find("azure"));
    }

    [Fact]
    public void StoreRemovesTheKeyFromEveryPlaceThatHoldsIt()
    {
        // Arrange
        var keychain = new FakeCredentialBackend("Test Keychain");
        var file = new FakeCredentialBackend("credentials file");
        keychain.Keys["openai"] = Secret;
        file.Keys["openai"] = Secret;
        keychain.Keys["kimi"] = Secret;
        var store = new CredentialStore([keychain], file);

        // Act
        var removed = store.Remove("openai");

        // Assert
        Assert.Equal(["Test Keychain", "credentials file"], removed);
        Assert.Null(store.Find("openai"));
        Assert.NotNull(store.Find("kimi"));
    }

    [Fact]
    public void WindowsCredentialManagerRoundTripsATemporaryTestEntry()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        var backend = new WindowsCredentialManagerBackend("CodeAtlas-Test-" + Guid.NewGuid().ToString("N") + ":");
        try
        {
            // Act
            var before = backend.Read("openai");
            backend.Write("openai", Secret);
            var read = backend.Read("openai");
            backend.Write("openai", Secret + "-replaced");
            var replaced = backend.Read("openai");
            var deleted = backend.Delete("openai");

            // Assert
            Assert.Null(before);
            Assert.Equal(Secret, read);
            Assert.Equal(Secret + "-replaced", replaced);
            Assert.True(deleted);
            Assert.Null(backend.Read("openai"));
            Assert.False(backend.Delete("openai"));
        }
        finally { backend.Delete("openai"); }
    }

    [Fact]
    public void WindowsCredentialManagerRejectsOversizedKeysWithoutWriting()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Arrange
        var backend = new WindowsCredentialManagerBackend("CodeAtlas-Test-" + Guid.NewGuid().ToString("N") + ":");

        // Act
        var error = Record.Exception(() => backend.Write("openai", new string('k', 3000)));

        // Assert
        Assert.IsType<CredentialStoreException>(error);
        Assert.Null(backend.Read("openai"));
    }

    private static ProcessResult MacKeychain(ProcessCall call, Dictionary<string, string> keychain)
    {
        var arguments = call.Arguments;
        if (arguments.SequenceEqual(["-i"]))
        {
            var match = Regex.Match(call.StandardInput!, "^add-generic-password -U -s \"CodeAtlas\" -a \"(\\w+)\" -l \"[^\"]*\" -w \"([^\"]+)\"\n$");
            if (!match.Success) return new(1, "", "parse error");
            keychain[match.Groups[1].Value] = match.Groups[2].Value;
            return new(0, "", "");
        }
        var account = arguments[Array.IndexOf(arguments.ToArray(), "-a") + 1];
        return arguments[0] switch
        {
            "find-generic-password" => keychain.TryGetValue(account, out var value) ? new(0, value + "\n", "") : new(44, "", "not found"),
            "delete-generic-password" => keychain.Remove(account) ? new(0, "", "") : new(44, "", "not found"),
            _ => new(2, "", "unexpected")
        };
    }

    private static ProcessResult SecretTool(ProcessCall call, Dictionary<string, string> secrets)
    {
        var provider = call.Arguments[^1];
        return call.Arguments[0] switch
        {
            "store" => Store(),
            "lookup" => secrets.TryGetValue(provider, out var value) ? new(0, value, "") : new(1, "", ""),
            "clear" => new(secrets.Remove(provider) ? 0 : 1, "", ""),
            _ => new(2, "", "unexpected")
        };

        ProcessResult Store()
        {
            secrets[provider] = call.StandardInput!;
            return new(0, "", "");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

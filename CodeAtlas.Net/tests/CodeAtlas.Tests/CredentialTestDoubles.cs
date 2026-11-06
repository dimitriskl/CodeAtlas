using CodeAtlas.Cli;

namespace CodeAtlas.Tests;

/// <summary>In-memory credential store; never touches the user's real keychain.</summary>
internal sealed class FakeCredentialStore(string name = "Fake Keychain") : ICredentialStore
{
    public Dictionary<string, string> Keys { get; } = new(StringComparer.Ordinal);
    public int Saves { get; private set; }
    public bool FailSave { get; init; }
    public bool Forbidden { get; init; }
    public Action? OnSave { get; init; }

    public CredentialLookup? Find(string provider)
    {
        if (Forbidden) throw new InvalidOperationException("Unexpected credential read");
        return Keys.TryGetValue(provider, out var value) ? new(value, name) : null;
    }

    public CredentialSaveResult Save(string provider, string secret)
    {
        if (Forbidden) throw new InvalidOperationException("Unexpected credential write");
        if (FailSave) throw new CredentialStoreException("Fake keychain is locked.");
        OnSave?.Invoke();
        Saves++;
        Keys[provider] = secret;
        return new(name, null);
    }

    public IReadOnlyList<string> Remove(string provider) => Keys.Remove(provider) ? [name] : [];
}

/// <summary>In-memory backend for composite-store tests.</summary>
internal sealed class FakeCredentialBackend(string name, bool available = true, bool failWrite = false) : ICredentialBackend
{
    public Dictionary<string, string> Keys { get; } = new(StringComparer.Ordinal);
    public string Name => name;
    public bool IsAvailable => available;
    public string? Read(string provider) => Keys.GetValueOrDefault(provider);

    public void Write(string provider, string secret)
    {
        if (failWrite) throw new CredentialStoreException(name + " write failed.");
        Keys[provider] = secret;
    }

    public bool Delete(string provider) => Keys.Remove(provider);
}

internal sealed record ProcessCall(string Command, IReadOnlyList<string> Arguments, string? StandardInput);

/// <summary>Records keychain tool invocations and answers them from a script.</summary>
internal sealed class FakeProcessRunner(Func<ProcessCall, ProcessResult> respond, params string[] existing) : IProcessRunner
{
    public List<ProcessCall> Calls { get; } = [];
    public bool Exists(string command) => existing.Contains(command);

    public ProcessResult Run(string command, IReadOnlyList<string> arguments, string? standardInput)
    {
        var call = new ProcessCall(command, arguments.ToArray(), standardInput);
        Calls.Add(call);
        return respond(call);
    }
}

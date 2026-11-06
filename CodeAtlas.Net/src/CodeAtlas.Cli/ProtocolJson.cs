using System.Text.Json;

namespace CodeAtlas.Cli;

/// <summary>
/// Checks a parsed request line of the line protocols, <c>mcp</c> and <c>live</c>, before anything reads it.
/// The parser accepts two things that only fail when they are read: an unpaired surrogate escape in a string or a
/// key, and a key repeated in one object, which <see cref="System.Text.Json.Nodes.JsonObject"/> refuses on its first
/// access. Checking the line once means no field needs its own catch.
/// </summary>
// Task #17: one unreadable line ended the server, because the failure came from reading a field, not from parsing.
internal static class ProtocolJson
{
    /// <summary>Whether every string and key under <paramref name="value"/> can be decoded and no object in it
    /// repeats a key. A value nests at most 64 levels, the parser's limit, so this recursion is bounded.</summary>
    public static bool IsReadable(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Decodes(value),
        JsonValueKind.Array => value.EnumerateArray().All(IsReadable),
        JsonValueKind.Object => HasReadableKeys(value) && value.EnumerateObject().All(property => IsReadable(property.Value)),
        _ => true
    };

    /// <summary>Whether the keys of one object can be decoded and are all different. Values are not looked at.</summary>
    public static bool HasReadableKeys(JsonElement value)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => KeyOrNull(property) is { } key && keys.Add(key));
    }

    /// <summary>Every value of <paramref name="name"/> in an object. A key that cannot be decoded is no match.</summary>
    public static JsonElement[] ValuesOf(JsonElement value, string name) =>
        [.. value.EnumerateObject().Where(property => KeyOrNull(property) == name).Select(property => property.Value)];

    private static string? KeyOrNull(JsonProperty property)
    {
        try { return property.Name; }
        catch (InvalidOperationException) { return null; }
    }

    private static bool Decodes(JsonElement text)
    {
        try { return text.GetString() is not null; }
        catch (InvalidOperationException) { return false; }
    }
}

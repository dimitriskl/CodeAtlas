namespace CodeAtlas;

public static class LanguageBoundary
{
    public static readonly string[] Supported = ["csharp"];

    public static bool Advertises(string language) =>
        Supported.Contains(language, StringComparer.OrdinalIgnoreCase);
}

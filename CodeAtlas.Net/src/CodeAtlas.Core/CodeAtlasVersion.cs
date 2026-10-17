using System.Reflection;

namespace CodeAtlas;

public static class CodeAtlasVersion
{
    public static string Current { get; } = typeof(CodeAtlasVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException("CodeAtlas version metadata is missing. Rebuild using Version.props.");
}

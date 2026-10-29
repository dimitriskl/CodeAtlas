using System.Xml.Linq;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class VersionTests
{
    [Fact]
    public void BuiltVersionMatchesSourceAndDocumentation()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CodeAtlas.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var version = XDocument.Load(Path.Combine(root.FullName, "Version.props"))
            .Descendants("CodeAtlasVersion").Single().Value;
        Assert.Matches("^[0-9]\\.[0-9]{2}\\.[0-9]{2}$", version);
        Assert.Equal(version, CodeAtlasVersion.Current);
        foreach (var path in new[] { "README.md", "docs/VERSION.md" })
            Assert.Contains($"Version: **{version}**", File.ReadAllText(Path.Combine(root.FullName, path)));
    }
}

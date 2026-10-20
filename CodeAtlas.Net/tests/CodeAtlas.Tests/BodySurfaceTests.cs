using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class BodySurfaceTests
{
    private static string? Hash(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview));
        return BodySurface.TryHash(tree.GetRoot(), tree.GetText());
    }

    [Theory]
    [InlineData("class C { int M() => 1; }", "class C { int M() =>\n 2 + N(); int N() => 0; }")]
    public void ADeclarationAddedAlongsideABodyEditNeverLooksLikeABodyOnlyEdit(string before, string after)
    {
        // Arrange
        var previous = Hash(before);

        // Act
        var current = Hash(after);

        // Assert
        Assert.NotEqual(previous, current);
    }

    [Theory]
    [InlineData("class C { int M() => 1; }", "class C { int M() =>\n 2 + 3; }")]
    [InlineData("class C { int M() { return 1; } }", "class C { int M() { int Local() => 2;\n return Local(); } }")]
    public void OrdinaryBodyAndLocalFunctionEditsPreserveTheStrictSurface(string before, string after)
    {
        // Arrange
        var previous = Hash(before);

        // Act
        var current = Hash(after);

        // Assert
        Assert.NotNull(previous);
        Assert.Equal(previous, current);
    }

    [Theory]
    [InlineData("class C { int M(int x) => x; }", "class C { int M(int x = 0) => x; }")]
    [InlineData("class C { int M() => 1; }", "class C { int M() { return 1; } }")]
    [InlineData("class C { int M() => 1; }", "class C { /// <see cref=\"M\"/>\n int M() => 1; }")]
    [InlineData("class C { int P => 1; }", "class C { int P => 2; }")]
    public void DeclarationDocumentationAndUnsupportedBodiesChangeTheSurface(string before, string after)
    {
        // Arrange
        var previous = Hash(before);

        // Act
        var current = Hash(after);

        // Assert
        Assert.NotEqual(previous, current);
    }

    [Fact]
    public void ABodyDirectiveCannotDisappearIntoTheStrippedBody()
    {
        // Arrange
        const string source = "class C { int M() {\n#nullable enable\n return 1; } }";

        // Act
        var surface = Hash(source);

        // Assert
        Assert.Null(surface);
    }
}

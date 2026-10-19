using Xunit;

namespace CodeAtlas.Tests;

public sealed class DeclaringTypeTests(IndexedSolution fixture) : IClassFixture<IndexedSolution>
{
    [Fact]
    public async Task RoslynMetadataPreservesNestedGenericAndExplicitInterfaceDeclaringTypes()
    {
        // Arrange
        var path = Path.Combine(fixture.Root, "Library", "Nested.cs");
        await File.WriteAllTextAsync(path, """
            namespace Metadata;
            public interface IRunner { void Run(); }
            public class Outer<T>
            {
                public class Inner<U> : IRunner
                {
                    public void Run() { }
                    void IRunner.Run() { }
                }
            }
            """);
        try
        {
            // Act
            var index = await SolutionAnalysis.CreateIndexAsync(fixture.Index.Solution);

            // Assert
            var nested = index.Methods.Where(method => method.Location.File == "Library/Nested.cs"
                && method.ContainingType == "Metadata.Outer<T>.Inner<U>").ToArray();
            Assert.Equal(2, nested.Length);
            Assert.Contains(nested, method => method.Id.Contains("#", StringComparison.Ordinal));
            Assert.All(nested, method => Assert.Equal("Metadata.Outer<T>.Inner<U>", method.ContainingType));
            Assert.Contains(index.Methods, method => method.ContainingType == "Metadata.IRunner");
        }
        finally { File.Delete(path); }
    }
}

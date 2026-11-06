using System.Collections.Immutable;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class ScopedReferencePartitionTests(SemanticSolution fixture) : IClassFixture<SemanticSolution>
{
    [Fact]
    public async Task EveryMethodsFullReferenceSetEqualsTheUnionOfItsScopedDocumentPartitions()
    {
        // Arrange
        var captured = await fixture.Analysis.CaptureIncrementalStateAsync(fixture.Index.IndexId,
            fixture.Index.InputManifest!, CancellationToken.None);
        var documents = captured.SearchDocuments.Values.DistinctBy(document => document.Id).ToArray();
        var partitions = documents.Select((document, index) => (document, index))
            .GroupBy(item => item.index % 3).Select(group => group.Select(item => item.document).ToImmutableHashSet()).ToArray();
        var expected = fixture.Index.References;

        // Act
        var actual = new Dictionary<string, SourceReference[]>();
        var escaped = new List<SourceReference>();
        foreach (var method in fixture.Index.Methods)
        {
            var accumulated = new List<SourceReference>();
            foreach (var partition in partitions)
            {
                var origins = captured.State.Projects.SelectMany(project => project.Trees)
                    .Where(tree => captured.SearchDocuments.TryGetValue(tree.Key, out var document) && partition.Contains(document))
                    .Select(tree => (tree.Project, tree.OriginPath)).ToHashSet();
                var references = await fixture.Analysis.FindReferencesCoreAsync(method.Id, partition);
                escaped.AddRange(references.Where(reference => !origins.Contains((reference.Project, reference.Location.File))));
                accumulated.AddRange(references);
            }
            actual.Add(method.Id, SolutionAnalysis.CanonicalReferences(accumulated));
        }

        // Assert
        Assert.Empty(escaped);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var method in fixture.Index.Methods)
            Assert.Equal(expected[method.Id], actual[method.Id]);
    }
}

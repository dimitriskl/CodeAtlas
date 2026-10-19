using Xunit;

namespace CodeAtlas.Tests;

public sealed class QueryPagingTests
{
    private static MethodInfo Method(string id, string project = "Library/Library.csproj", string? type = "Sample.Calculator") =>
        new(id, "Run", "Run()", project, new("Library/Source.cs", 1, 1)) { ContainingType = type };

    [Fact]
    public void MethodPagesExactlyPartitionOrdinalIdentityOrder()
    {
        // Arrange
        var methods = new[] { Method("z"), Method("B"), Method("a"), Method("A") };
        var expected = methods.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
        var request = new QueryRequest(Limit: 2);

        // Act
        var first = QueryPaging.Methods(methods, request, "snapshot");
        var second = QueryPaging.Methods(methods, request with { Offset = 2, Snapshot = "snapshot" }, "snapshot");

        // Assert
        Assert.Equal(expected, first.Items.Concat(second.Items));
        Assert.Equal(4, first.Pagination.Total);
        Assert.True(first.Pagination.HasMore);
        Assert.Equal(2, first.Pagination.NextOffset);
        Assert.False(second.Pagination.HasMore);
        Assert.Null(second.Pagination.NextOffset);
        Assert.Equal("snapshot", second.Pagination.SnapshotId);
    }

    [Fact]
    public void ReferencePagesPreserveTiedLocationsAndEveryIdentityComponent()
    {
        // Arrange
        var location = new SourcePosition("Shared.cs", 10, 2);
        var references = new[]
        {
            new SourceReference("definition-b", "project-a", "reference", location),
            new SourceReference("definition-a", "project-b", "reference", location),
            new SourceReference("definition-a", "project-a", "reference", location),
            new SourceReference("definition-a", "project-a", "invocation", location)
        };
        var expected = new[] { references[3], references[2], references[1], references[0] };
        var request = new QueryRequest(Limit: 1, Snapshot: "snapshot");

        // Act
        var pages = Enumerable.Range(0, 4)
            .Select(offset => QueryPaging.References(references, request with { Offset = offset }, "snapshot")).ToArray();

        // Assert
        Assert.Equal(expected, pages.SelectMany(page => page.Items));
        Assert.All(pages, page => Assert.Equal(4, page.Pagination.Total));
        Assert.Equal(4, pages.SelectMany(page => page.Items).Distinct().Count());
    }

    [Fact]
    public void ExactProjectAndDeclaringTypeFiltersRunBeforePagination()
    {
        // Arrange
        var methods = new[] { Method("a", "Other.csproj"), Method("b", type: "Other.Type"), Method("c"), Method("d") };
        var request = new QueryRequest(Project: "Library/Library.csproj", Type: "Sample.Calculator", Limit: 1);

        // Act
        var page = QueryPaging.Methods(methods, request, "snapshot");

        // Assert
        Assert.Equal("c", Assert.Single(page.Items).Id);
        Assert.Equal(2, page.Pagination.Total);
        Assert.True(page.Pagination.HasMore);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1001, 0)]
    [InlineData(1, -1)]
    public void InvalidBoundsAreRejected(int limit, int offset)
    {
        // Arrange
        var request = new QueryRequest(Limit: limit, Offset: offset);

        // Act
        var exception = Record.Exception(() => QueryPaging.Methods([], request, "snapshot"));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    [Fact]
    public void ContinuationRequiresSameSnapshotEvenWhenResultsAreEmpty()
    {
        // Arrange
        var missing = new QueryRequest(Offset: 1);
        var wrong = new QueryRequest(Offset: 1, Snapshot: "previous");

        // Act
        var missingError = Record.Exception(() => QueryPaging.Methods([], missing, "current"));
        var wrongError = Record.Exception(() => QueryPaging.Methods([], wrong, "current"));

        // Assert
        Assert.IsType<ArgumentException>(missingError);
        Assert.Equal("snapshot_mismatch", Assert.IsType<QueryException>(wrongError).Code);
    }

    [Fact]
    public void MaximumOffsetDoesNotOverflowAndReturnsAnExhaustedPage()
    {
        // Arrange
        var request = new QueryRequest(Offset: int.MaxValue, Snapshot: "snapshot");

        // Act
        var page = QueryPaging.Methods([Method("a")], request, "snapshot");

        // Assert
        Assert.Empty(page.Items);
        Assert.Equal(1, page.Pagination.Total);
        Assert.False(page.Pagination.HasMore);
        Assert.Null(page.Pagination.NextOffset);
    }

    [Fact]
    public void LegacyMetadataAllowsOrdinaryPagingButRequiresRebuildForTypeFilter()
    {
        // Arrange
        var methods = new[] { Method("legacy", type: null) };
        var ordinary = new QueryRequest();
        var filtered = new QueryRequest(Type: "Sample.Calculator");

        // Act
        var page = QueryPaging.Methods(methods, ordinary, "snapshot");
        var exception = Record.Exception(() => QueryPaging.Methods(methods, filtered, "snapshot"));

        // Assert
        Assert.Single(page.Items);
        Assert.Equal("rebuild_required", Assert.IsType<QueryException>(exception).Code);
    }

    [Fact]
    public void LiveFirstPageReportsTruncationWithoutOfferingAContinuation()
    {
        // Arrange
        var request = new QueryRequest(Limit: 1);

        // Act
        var page = QueryPaging.Methods([Method("a"), Method("b")], request, snapshotId: null);

        // Assert
        Assert.True(page.Pagination.HasMore);
        Assert.False(page.Pagination.ContinuationSupported);
        Assert.Null(page.Pagination.NextOffset);
        Assert.Null(page.Pagination.SnapshotId);
    }

    [Fact]
    public void ReferenceProjectFilterAppliesToReferencingProject()
    {
        // Arrange
        var location = new SourcePosition("Shared.cs", 10, 2);
        var references = new[] { new SourceReference("Library::M:Run", "Caller.csproj", "invocation", location),
            new SourceReference("Library::M:Run", "Library.csproj", "reference", location) };
        var request = new QueryRequest(Project: "Caller.csproj");

        // Act
        var page = QueryPaging.References(references, request, "snapshot");

        // Assert
        Assert.Equal(references[0], Assert.Single(page.Items));
    }
}

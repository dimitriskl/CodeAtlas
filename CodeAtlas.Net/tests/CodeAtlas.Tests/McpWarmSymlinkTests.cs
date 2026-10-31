using System.Text.Json.Nodes;
using Xunit;
using static CodeAtlas.Tests.McpLines;

namespace CodeAtlas.Tests;

/// <summary>
/// An MSBuild import is tracked by the path the build used, which can be a link to a file outside the
/// solution. A warm server has to answer as a new one does whatever happens behind that link.
/// </summary>
public sealed class McpWarmSymlinkTests(IndexedSolution fixture) : IClassFixture<IndexedSolution>
{
    private const string EditTarget = "edit-target";
    private const string DeleteTarget = "delete-target";
    private const string Retarget = "retarget";
    private const string LinkLoop = "link-loop";
    private string Solution => Path.Combine(fixture.Root, "Sample.slnx");
    private string Imported => Path.Combine(fixture.Root, "Version.props");

    [Theory]
    [InlineData(EditTarget, "stale_index")]
    [InlineData(DeleteTarget, "stale_index")]
    [InlineData(Retarget, "stale_index")]
    [InlineData(LinkLoop, "snapshot_io_failed")]
    public async Task AChangeBehindALinkAnswersLikeANewServer(string change, string code)
    {
        // Arrange
        var original = await File.ReadAllBytesAsync(Imported);
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Parent, "outside-" + Guid.NewGuid().ToString("N"))).FullName;
        var target = Path.Combine(outside, "Version.props");
        try
        {
            // Same bytes and path as the indexed file, so the published generation stays fresh.
            await File.WriteAllBytesAsync(target, original);
            File.Delete(Imported);
            SqliteFileUpdateTests.Link(Imported, target, directory: false);

            // Act
            var (first, warm, restarted) = await AskAcrossAsync(() => Apply(change, target));

            // Assert
            Assert.False(Envelope(first, isError: false)["freshness"]!["isStale"]!.GetValue<bool>());
            var warmAnswer = Envelope(warm, isError: true);
            var newAnswer = Envelope(restarted, isError: true);
            Assert.Equal(code, warmAnswer["error"]!["code"]!.GetValue<string>());
            Assert.Equal(code, newAnswer["error"]!["code"]!.GetValue<string>());
            Assert.Equal(newAnswer["freshness"]!["changedFiles"]!.ToJsonString(), warmAnswer["freshness"]!["changedFiles"]!.ToJsonString());
        }
        finally
        {
            File.Delete(Imported);
            await File.WriteAllBytesAsync(Imported, original);
            Directory.Delete(outside, recursive: true);
        }
    }

    private async Task<(JsonObject First, JsonObject Warm, JsonObject Restarted)> AskAcrossAsync(Action change)
    {
        var lines = new[] { Call(1, fixture.Root, "Calculate"), Call(2, fixture.Root, "Calculate") };
        var warm = await Talk(Solution, lines, beforeLine: index =>
        {
            if (index == 1) change();
        });
        var restarted = await Talk(Solution, lines[..1]);
        return (warm[0], warm[1], restarted[0]);
    }

    private void Apply(string change, string target)
    {
        var other = Path.Combine(Path.GetDirectoryName(target)!, "Other.props");
        switch (change)
        {
            case EditTarget:
                File.AppendAllText(target, "<!-- edited -->");
                break;
            case DeleteTarget:
                File.Delete(target);
                break;
            case Retarget:
                File.WriteAllText(other, "<Project />");
                File.Delete(Imported);
                SqliteFileUpdateTests.Link(Imported, other, directory: false);
                break;
            case LinkLoop:
                File.Delete(Imported);
                SqliteFileUpdateTests.Link(other, Imported, directory: false);
                SqliteFileUpdateTests.Link(Imported, other, directory: false);
                break;
        }
    }
}

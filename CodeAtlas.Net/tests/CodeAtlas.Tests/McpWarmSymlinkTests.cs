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
    private const string AddChild = "add-child";
    private const string RemoveChild = "remove-child";
    private const string AddFile = "add-file";
    private const string RemoveFile = "remove-file";
    private const string BreakChain = "break-chain";
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

    // An inventory lists child directories only, so an added or removed file moves the stamp, the full
    // check finds nothing, and both servers stay fresh. Which error a broken link or a cycle gives depends
    // on the OS, because a link's own attributes differ there, so those rows pin that both servers agree.
    [Theory]
    [InlineData(AddChild, true)]
    [InlineData(RemoveChild, true)]
    [InlineData(AddFile, false)]
    [InlineData(RemoveFile, false)]
    [InlineData(Retarget, true)]
    [InlineData(BreakChain, true)]
    [InlineData(LinkLoop, true)]
    public async Task AChangeBehindALinkedInventoryAnswersLikeANewServer(string change, bool refused)
    {
        // Arrange
        var root = Directory.CreateDirectory(Path.Combine(fixture.Parent, "inventory-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            await fixture.PublishTrackingAsync([Imported], [CreateLinkedInventory(root)]);

            // Act
            var (first, warm, restarted) = await AskAcrossAsync(() => ApplyBehindInventory(change, root));

            // Assert
            Assert.False(Envelope(first, isError: false)["freshness"]!["isStale"]!.GetValue<bool>());
            var warmAnswer = Envelope(warm, isError: refused);
            var newAnswer = Envelope(restarted, isError: refused);
            Assert.Equal(newAnswer["error"]?["code"]?.GetValue<string>(), warmAnswer["error"]?["code"]?.GetValue<string>());
            Assert.Equal(newAnswer["freshness"]!["changedFiles"]!.ToJsonString(), warmAnswer["freshness"]!["changedFiles"]!.ToJsonString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            await fixture.RestorePublicationAsync();
        }
    }

    // The chain has two links, so a change at its end or in its middle leaves the recorded link untouched.
    private static string CreateLinkedInventory(string root)
    {
        var inventory = Path.Combine(root, "inventory");
        Directory.CreateDirectory(Path.Combine(root, "first", "10.0.100"));
        File.WriteAllText(Path.Combine(root, "first", "kept.txt"), "kept");
        Directory.CreateDirectory(Path.Combine(root, "second", "10.0.200"));
        SqliteFileUpdateTests.Link(Path.Combine(root, "hop"), Path.Combine(root, "first"), directory: true);
        SqliteFileUpdateTests.Link(inventory, Path.Combine(root, "hop"), directory: true);
        return inventory;
    }

    private static void ApplyBehindInventory(string change, string root)
    {
        var first = Path.Combine(root, "first");
        var hop = Path.Combine(root, "hop");
        switch (change)
        {
            case AddChild:
                Directory.CreateDirectory(Path.Combine(first, "10.0.200"));
                break;
            case RemoveChild:
                Directory.Delete(Path.Combine(first, "10.0.100"));
                break;
            case AddFile:
                File.WriteAllText(Path.Combine(first, "added.txt"), "added");
                break;
            case RemoveFile:
                File.Delete(Path.Combine(first, "kept.txt"));
                break;
            case Retarget:
                Directory.Delete(hop);
                SqliteFileUpdateTests.Link(hop, Path.Combine(root, "second"), directory: true);
                break;
            case BreakChain:
                Directory.Delete(first, recursive: true);
                break;
            case LinkLoop:
                Directory.Delete(hop);
                SqliteFileUpdateTests.Link(hop, Path.Combine(root, "inventory"), directory: true);
                break;
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

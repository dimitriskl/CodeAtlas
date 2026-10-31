using Xunit;

namespace CodeAtlas.Tests;

public sealed class InputStampsTests : IDisposable
{
    private static readonly BuildInputEvidence Evidence = new(true, true, true, true, true, true, [], []);
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("codeatlas-input-stamps-");

    private string PathOf(string name) => Path.Combine(root.FullName, name);

    private async Task<InputStamps> StampAsync(params string[] files)
    {
        var manifest = await SnapshotInputModel.CaptureAsync(files, [], [], new Dictionary<string, string>(), Evidence);
        return new InputStamps(manifest, PathOf("Sample.slnx"));
    }

    [Fact]
    public async Task EditingTheFileAtTheEndOfALinkChainIsSeen()
    {
        // Arrange
        var file = PathOf("file.txt");
        await File.WriteAllTextAsync(file, "one");
        var middle = PathOf("middle.txt");
        var link = PathOf("link.txt");
        SqliteFileUpdateTests.Link(middle, file, directory: false);
        SqliteFileUpdateTests.Link(link, middle, directory: false);
        var stamps = await StampAsync(link);
        await File.WriteAllTextAsync(file, "edited");

        // Act
        var changed = await stamps.ChangedFilesAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["file.txt"], changed);
    }

    [Fact]
    public async Task ALinkPointedAtAnotherFileWithTheSameStampIsHashedAgain()
    {
        // Arrange
        var first = PathOf("first.txt");
        var second = PathOf("second.txt");
        await File.WriteAllTextAsync(first, "one");
        await File.WriteAllTextAsync(second, "two");
        File.SetLastWriteTimeUtc(second, File.GetLastWriteTimeUtc(first));
        var link = PathOf("link.txt");
        SqliteFileUpdateTests.Link(link, first, directory: false);
        var stamps = await StampAsync(link);
        File.Delete(link);
        SqliteFileUpdateTests.Link(link, second, directory: false);

        // Act
        var changed = await stamps.ChangedFilesAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["second.txt"], changed);
    }

    [Fact]
    public async Task ALinkPointedAtAnIdenticalCopyStaysUnchanged()
    {
        // Arrange
        var first = PathOf("first.txt");
        var second = PathOf("second.txt");
        await File.WriteAllTextAsync(first, "one");
        await File.WriteAllTextAsync(second, "one");
        var link = PathOf("link.txt");
        SqliteFileUpdateTests.Link(link, first, directory: false);
        var stamps = await StampAsync(link);
        File.Delete(link);
        SqliteFileUpdateTests.Link(link, second, directory: false);

        // Act
        var changed = await stamps.ChangedFilesAsync(CancellationToken.None);

        // Assert
        Assert.Empty(changed);
    }

    [Fact]
    public async Task ALinkWhoseTargetIsDeletedIsChanged()
    {
        // Arrange
        var file = PathOf("file.txt");
        await File.WriteAllTextAsync(file, "one");
        var link = PathOf("link.txt");
        SqliteFileUpdateTests.Link(link, file, directory: false);
        var stamps = await StampAsync(link);
        File.Delete(file);

        // Act
        var changed = await stamps.ChangedFilesAsync(CancellationToken.None);

        // Assert
        Assert.Equal(["file.txt"], changed);
    }

    [Fact]
    public async Task ADanglingLinkIsUnchangedUntilItsTargetAppears()
    {
        // Arrange
        var file = PathOf("file.txt");
        var link = PathOf("link.txt");
        SqliteFileUpdateTests.Link(link, file, directory: false);
        var stamps = await StampAsync(link);
        var whileMissing = await stamps.ChangedFilesAsync(CancellationToken.None);
        await File.WriteAllTextAsync(file, "one");

        // Act
        var changed = await stamps.ChangedFilesAsync(CancellationToken.None);

        // Assert
        Assert.Empty(whileMissing);
        Assert.Equal(["file.txt"], changed);
    }

    public void Dispose() => root.Delete(recursive: true);
}

using Xunit;

namespace CodeAtlas.Tests;

public sealed class InputStampsTests : IDisposable
{
    private const long BigFileLength = 2L * 1024 * 1024 * 1024;
    private static readonly BuildInputEvidence Evidence = new(true, true, true, true, true, true, [], []);
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("codeatlas-input-stamps-");

    /// <summary>
    /// How long a test waits before it edits a file while a big file is hashed. Hashing takes seconds, so
    /// the edit lands during it. If a slow machine lands it elsewhere, correct code still passes.
    /// </summary>
    internal static readonly TimeSpan EditDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>A file that takes seconds to hash. Its length is set, not written, so creating it is quick.</summary>
    internal static void WriteBigFile(string path)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        file.SetLength(BigFileLength);
    }

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

    [Fact]
    public async Task AnEditMadeWhileAnotherFileIsHashedIsSeenByTheNextCheck()
    {
        // Arrange
        var small = PathOf("a-small.json");
        var big = PathOf("z-big.json");
        await File.WriteAllTextAsync(small, "{}");
        WriteBigFile(big);
        var stamps = await StampAsync(small, big);
        File.SetLastWriteTimeUtc(big, File.GetLastWriteTimeUtc(big).AddMinutes(-5));

        // Act
        var changed = await CheckAfterAnEditDuringAHashAsync(stamps, small);

        // Assert
        Assert.Equal(["a-small.json"], changed);
    }

    private static async Task<string[]> CheckAfterAnEditDuringAHashAsync(InputStamps stamps, string file)
    {
        var hashing = Task.Run(() => stamps.ChangedFilesAsync(CancellationToken.None));
        await Task.Delay(EditDelay);
        await File.WriteAllTextAsync(file, "edited");
        await hashing;
        return await stamps.ChangedFilesAsync(CancellationToken.None);
    }

    public void Dispose() => root.Delete(recursive: true);
}

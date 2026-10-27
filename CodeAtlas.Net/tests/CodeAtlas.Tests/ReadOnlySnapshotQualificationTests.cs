using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using Xunit;

namespace CodeAtlas.Tests;

public sealed class ReadOnlySnapshotQualificationTests(IndexedSolution fixture) : IClassFixture<IndexedSolution>
{
    [Fact]
    public async Task WindowsDeniedTrackedSourceReadAtFinalVerificationPreservesPreviousSnapshot()
    {
        if (!OperatingSystem.IsWindows())
            return;
        await VerifyDeniedSourceReadAtCommit();
    }

    [SupportedOSPlatform("windows")]
    private async Task VerifyDeniedSourceReadAtCommit()
    {
        // Arrange
        var attemptedIndex = SnapshotTestIdentity.NewGeneration(fixture.Index);
        var pointerBefore = await File.ReadAllBytesAsync(fixture.IndexPath);
        var source = new FileInfo(Path.Combine(fixture.Root, "Library", "Calculator.cs"));
        var original = source.GetAccessControl(AccessControlSections.Access);
        var denied = new FileSecurity();
        denied.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ReadData, AccessControlType.Deny));
        var generationDirectory = Path.Combine(Path.GetDirectoryName(fixture.IndexPath)!, "generations", attemptedIndex.IndexId);
        var precedingArtifacts = new[] { "index.db", "overview.md", "warnings.txt" };
        var callbackReached = false;
        string[] stagedArtifacts = [];
        async Task WriteTimingsAndDenySourceRead(Stream stream, CancellationToken token)
        {
            stagedArtifacts = precedingArtifacts.Where(name => File.Exists(Path.Combine(generationDirectory, name))).ToArray();
            await JsonSerializer.SerializeAsync(stream, new { indexId = attemptedIndex.IndexId }, cancellationToken: token);
            SetSecurity(source, denied);
            callbackReached = true;
        }

        try
        {
            // Act
            var failure = await Record.ExceptionAsync(() => IndexStore.PublishAsync(attemptedIndex, WriteTimingsAndDenySourceRead));
            var sourceReadFailure = Record.Exception(() => File.ReadAllText(source.FullName));
            var freshness = await SnapshotInputs.CheckFreshnessAsync(attemptedIndex);
            var pointerAfter = await File.ReadAllBytesAsync(fixture.IndexPath);
            var current = await IndexStore.LoadAsync(fixture.IndexPath);

            // Assert
            Assert.True(callbackReached);
            Assert.Equal(precedingArtifacts, stagedArtifacts);
            Assert.IsType<UnauthorizedAccessException>(sourceReadFailure);
            Assert.Equal("unknown", freshness.Status);
            Assert.False(freshness.IsStale);
            Assert.NotEmpty(freshness.Diagnostics);
            Assert.Equal("snapshot_io_failed", Assert.IsType<IndexFormatException>(failure).Code);
            Assert.Equal(pointerBefore, pointerAfter);
            Assert.Equal(fixture.Index.IndexId, current.IndexId);
        }
        finally
        {
            SetSecurity(source, original);
        }
    }

    [Fact]
    public async Task WindowsDenyWriteAclRejectsPublicationButAllowsStatusAndQueriesWithoutChangingStorage()
    {
        if (!OperatingSystem.IsWindows())
            return;
        await VerifyWindowsReadOnlyQueries();
    }

    [SupportedOSPlatform("windows")]
    private async Task VerifyWindowsReadOnlyQueries()
    {
        // Arrange
        var storage = Path.Combine(fixture.Root, ".codeatlas");
        var files = Directory.GetFiles(storage, "*", SearchOption.AllDirectories);
        var before = files.ToDictionary(path => path, File.ReadAllBytes);
        var directories = Directory.GetDirectories(storage, "*", SearchOption.AllDirectories).Prepend(storage).ToArray();
        var identity = WindowsIdentity.GetCurrent().User!;
        var denied = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;
        var security = new List<(FileSystemInfo Item, FileSystemSecurity Original)>();
        var symbol = fixture.Index.Methods.First(method => method.Name == "Calculate").Id;
        var attemptedIndex = SnapshotTestIdentity.NewGeneration(fixture.Index);
        try
        {
            // Explicit denies on every item avoid relying on inheritance or file attributes.
            foreach (var path in directories.Concat(files))
            {
                FileSystemInfo item = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                FileSystemSecurity original = item is DirectoryInfo directory
                    ? directory.GetAccessControl(AccessControlSections.Access)
                    : ((FileInfo)item).GetAccessControl(AccessControlSections.Access);
                security.Add((item, original));
                FileSystemSecurity changed = item is DirectoryInfo
                    ? new DirectorySecurity() : new FileSecurity();
                changed.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                changed.AddAccessRule(new FileSystemAccessRule(identity, denied, AccessControlType.Deny));
                SetSecurity(item, changed);
            }
            var existing = files.First(path => path.EndsWith("current.json", StringComparison.Ordinal));

            // Act
            var createFailure = Record.Exception(() => File.WriteAllText(Path.Combine(storage, "write-probe.tmp"), "forbidden"));
            var appendFailure = Record.Exception(() => File.AppendAllText(existing, "forbidden"));
            var deleteFailure = Record.Exception(() => File.Delete(existing));
            var publicationFailure = await Record.ExceptionAsync(() => IndexStore.PublishAsync(attemptedIndex));
            var status = await RunCli("index-status", "--index", fixture.IndexPath);
            var symbols = await RunCli("find-symbol", "--index", fixture.IndexPath, "--name", "Calculate");
            var references = await RunCli("find-references", "--index", fixture.IndexPath, "--symbol", symbol);
            var afterFiles = Directory.GetFiles(storage, "*", SearchOption.AllDirectories);
            var after = afterFiles.ToDictionary(path => path, File.ReadAllBytes);
            var afterDirectories = Directory.GetDirectories(storage, "*", SearchOption.AllDirectories).Prepend(storage).ToArray();

            // Assert
            Assert.IsType<UnauthorizedAccessException>(createFailure);
            Assert.IsType<UnauthorizedAccessException>(appendFailure);
            Assert.IsType<UnauthorizedAccessException>(deleteFailure);
            Assert.Equal("snapshot_io_failed", Assert.IsType<IndexFormatException>(publicationFailure).Code);
            foreach (var result in new[] { status, symbols, references })
            {
                Assert.True(result.Exit == 0, result.Stderr);
                Assert.True(JsonNode.Parse(result.Stdout)!["success"]!.GetValue<bool>());
                Assert.Empty(result.Stderr);
            }
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            Assert.Equal(directories.Order(), afterDirectories.Order());
            foreach (var (path, bytes) in before)
                Assert.Equal(bytes, after[path]);
        }
        finally
        {
            foreach (var (item, original) in security.AsEnumerable().Reverse())
                SetSecurity(item, original);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SetSecurity(FileSystemInfo item, FileSystemSecurity security)
    {
        // GetAccessControl returns an unmodified object; explicitly mark the access section
        // modified when restoring it so SetAccessControl persists the original DACL.
        FileSystemSecurity copy = item is DirectoryInfo ? new DirectorySecurity() : new FileSecurity();
        copy.SetSecurityDescriptorBinaryForm(security.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        if (item is DirectoryInfo directory)
            directory.SetAccessControl((DirectorySecurity)copy);
        else
            ((FileInfo)item).SetAccessControl((FileSecurity)copy);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunCli(params string[] arguments)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CodeAtlas.slnx")))
            repository = repository.Parent;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(repository!.FullName, "src", "CodeAtlas.Cli", "bin", configuration, "net10.0", "codeatlas.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }
}

using System.Text;
using System.Text.Json;
using SvnFlux.Core;

namespace SvnFlux.Repository.FileSystem.Tests;

public sealed class SvnFileSystemRepositoryTests
{
    [Fact]
    public async Task CreateAndReopenProducesRevisionZero()
    {
        using var directory = new TemporaryDirectory();
        var created = await SvnFileSystemRepository.CreateAsync(directory.Path);

        var reopened = await SvnFileSystemRepository.OpenAsync(directory.Path);
        var revision = await reopened.GetLatestRevisionAsync();
        var root = await reopened.OpenRevisionAsync(revision);

        Assert.Equal(created.Id, reopened.Id);
        Assert.Equal(0, revision.Value);
        Assert.Equal(SvnNodeKind.Directory, (await root.GetNodeInfoAsync(new SvnRepositoryPath("")))?.Kind);
        Assert.True(Directory.Exists(System.IO.Path.Combine(directory.Path, "revisions", "000000", "tree")));
    }

    [Fact]
    public async Task UnchangedFileSharesEarlierPhysicalBody()
    {
        using var directory = new TemporaryDirectory();
        var repository = await SvnFileSystemRepository.CreateAsync(directory.Path);
        await repository.CreateRevisionAsync(
            [SvnFileSystemChange.Write(new SvnRepositoryPath("readme.txt"), "original\n"u8)],
            RevisionProperties("Add readme"));

        await repository.CreateRevisionAsync([], RevisionProperties("Reuse readme"));

        var revisionTwoFile = System.IO.Path.Combine(directory.Path, "revisions", "000002", "tree", "readme.txt");
        await File.WriteAllTextAsync(revisionTwoFile, "shared\n", new UTF8Encoding(false));

        Assert.Equal(
            "shared\n",
            await ReadFileAsync(repository, new SvnRevision(1), new SvnRepositoryPath("readme.txt")));
        Assert.Equal("hard-links", JsonDocument.Parse(await File.ReadAllTextAsync(System.IO.Path.Combine(directory.Path, "format.json"))).RootElement.GetProperty("linkMode").GetString());
    }

    [Fact]
    public async Task ManualWriteThroughLinkMutatesEveryRevisionSharingBody()
    {
        using var directory = new TemporaryDirectory();
        var repository = await SvnFileSystemRepository.CreateAsync(directory.Path);
        var path = new SvnRepositoryPath("readme.txt");
        await repository.CreateRevisionAsync(
            [SvnFileSystemChange.Write(path, "original\n"u8)],
            RevisionProperties("Add readme"));
        await repository.CreateRevisionAsync([], RevisionProperties("Reuse readme"));

        var revisionTwoFile = System.IO.Path.Combine(directory.Path, "revisions", "000002", "tree", "readme.txt");
        await File.WriteAllTextAsync(revisionTwoFile, "manually changed\n", new UTF8Encoding(false));

        Assert.Equal("manually changed\n", await ReadFileAsync(repository, new SvnRevision(1), path));
        Assert.Equal("manually changed\n", await ReadFileAsync(repository, new SvnRevision(2), path));

        var reopened = await SvnFileSystemRepository.OpenAsync(directory.Path);
        Assert.Equal("manually changed\n", await ReadFileAsync(reopened, new SvnRevision(1), path));
        Assert.Equal("manually changed\n", await ReadFileAsync(reopened, new SvnRevision(2), path));
    }

    [Fact]
    public async Task ChangedFileGetsNewOrdinaryBody()
    {
        using var directory = new TemporaryDirectory();
        var repository = await SvnFileSystemRepository.CreateAsync(directory.Path);
        var path = new SvnRepositoryPath("readme.txt");
        await repository.CreateRevisionAsync(
            [SvnFileSystemChange.Write(path, "one"u8)],
            RevisionProperties("Add"));
        await repository.CreateRevisionAsync(
            [SvnFileSystemChange.Write(path, "two"u8)],
            RevisionProperties("Modify"));

        var revisionTwoFile = System.IO.Path.Combine(directory.Path, "revisions", "000002", "tree", "readme.txt");
        Assert.Null(new FileInfo(revisionTwoFile).LinkTarget);
        Assert.Equal("one", await ReadFileAsync(repository, new SvnRevision(1), path));
        Assert.Equal("two", await ReadFileAsync(repository, new SvnRevision(2), path));
    }

    [Fact]
    public async Task ReadsDirectoryAndLogFromPublishedRevision()
    {
        using var directory = new TemporaryDirectory();
        var repository = await SvnFileSystemRepository.CreateAsync(directory.Path);
        await repository.CreateRevisionAsync(
            [SvnFileSystemChange.Write(new SvnRepositoryPath("src/program.txt"), "program"u8)],
            RevisionProperties("Add program"));

        var root = await repository.OpenRevisionAsync(new SvnRevision(1));
        var rootEntries = await root.GetDirectoryAsync(new SvnRepositoryPath("")).ToListAsync();
        var sourceEntries = await root.GetDirectoryAsync(new SvnRepositoryPath("src")).ToListAsync();
        var log = await repository.GetLogAsync(new SvnLogQuery([new SvnRepositoryPath("")], new SvnRevision(1), new SvnRevision(0))).ToListAsync();

        Assert.Equal("src", Assert.Single(rootEntries).Name);
        Assert.Equal("program.txt", Assert.Single(sourceEntries).Name);
        Assert.Equal("Add program", Assert.Single(log).RevisionProperties.LogMessage);
        Assert.Equal("src/program.txt", Assert.Single(log[0].ChangedPaths).Path.Value);
    }

    [Fact]
    public async Task WritableContractCommitsModifyAddDeleteAndEmptyDirectory()
    {
        using var directory = new TemporaryDirectory();
        ISvnWritableRepository repository = await SvnFileSystemRepository.CreateAsync(directory.Path);
        var first = await repository.CommitAsync(new SvnCommitRequest(
            new SvnRevision(0),
            RevisionProperties("Initial"),
            [
                SvnCommitChange.AddFile(new SvnRepositoryPath("modify.txt"), "old"u8),
                SvnCommitChange.AddFile(new SvnRepositoryPath("delete.txt"), "remove"u8)
            ]));

        var second = await repository.CommitAsync(new SvnCommitRequest(
            first,
            RevisionProperties("All operations"),
            [
                SvnCommitChange.ModifyFile(new SvnRepositoryPath("modify.txt"), "new"u8),
                SvnCommitChange.AddFile(new SvnRepositoryPath("added.txt"), "added"u8),
                SvnCommitChange.AddDirectory(new SvnRepositoryPath("empty")),
                SvnCommitChange.Delete(new SvnRepositoryPath("delete.txt"), SvnNodeKind.File)
            ]));

        var root = await repository.OpenRevisionAsync(second);
        Assert.Equal("new", await ReadFileAsync(repository, second, new SvnRepositoryPath("modify.txt")));
        Assert.Equal("added", await ReadFileAsync(repository, second, new SvnRepositoryPath("added.txt")));
        Assert.Equal(SvnNodeKind.Directory, (await root.GetNodeInfoAsync(new SvnRepositoryPath("empty")))?.Kind);
        Assert.Null(await root.GetNodeInfoAsync(new SvnRepositoryPath("delete.txt")));
    }

    [Fact]
    public async Task WritableContractRejectsOutOfDateBaseRevision()
    {
        using var directory = new TemporaryDirectory();
        ISvnWritableRepository repository = await SvnFileSystemRepository.CreateAsync(directory.Path);
        await repository.CommitAsync(new SvnCommitRequest(new SvnRevision(0), RevisionProperties("First"), []));

        await Assert.ThrowsAsync<SvnOutOfDateException>(async () =>
            await repository.CommitAsync(new SvnCommitRequest(new SvnRevision(0), RevisionProperties("Stale"), [])));
    }

    [Fact]
    public async Task CopyPreservesContentAndAncestry() {
        using var directory = new TemporaryDirectory();
        ISvnWritableRepository repository = await SvnFileSystemRepository.CreateAsync(directory.Path);
        var first = await repository.CommitAsync(new SvnCommitRequest(new SvnRevision(0), RevisionProperties("Source"), [SvnCommitChange.AddFile(new SvnRepositoryPath("source.txt"), "body"u8)]));

        var second = await repository.CommitAsync(new SvnCommitRequest(first, RevisionProperties("Copy"), [SvnCommitChange.Copy(new SvnRepositoryPath("copy.txt"), SvnNodeKind.File, new SvnCopySource(new SvnRepositoryPath("source.txt"), first))]));

        Assert.Equal("body", await ReadFileAsync(repository, second, new SvnRepositoryPath("copy.txt")));
        var log = await repository.GetLogAsync(new SvnLogQuery([new SvnRepositoryPath("")], second, second)).ToListAsync();
        var change = Assert.Single(Assert.Single(log).ChangedPaths);
        Assert.Equal("source.txt", change.CopyFromPath?.Value);
        Assert.Equal(first, change.CopyFromRevision);
    }

    [Fact]
    public async Task PropertyChangesSetAndDeleteBinaryValues() {
        using var directory = new TemporaryDirectory();
        ISvnWritableRepository repository = await SvnFileSystemRepository.CreateAsync(directory.Path);
        var path = new SvnRepositoryPath("file.txt");
        var first = await repository.CommitAsync(new SvnCommitRequest(new SvnRevision(0), RevisionProperties("Add"), [SvnCommitChange.AddFile(path, "body"u8)]));
        var second = await repository.CommitAsync(new SvnCommitRequest(first, RevisionProperties("Set"), [SvnCommitChange.ModifyProperties(path, SvnNodeKind.File, [SvnPropertyChange.Set("custom:value", [0, 1, 255])])]));
        var third = await repository.CommitAsync(new SvnCommitRequest(second, RevisionProperties("Delete"), [SvnCommitChange.ModifyProperties(path, SvnNodeKind.File, [SvnPropertyChange.Delete("custom:value")])]));

        var secondRoot = await repository.OpenRevisionAsync(second);
        var thirdRoot = await repository.OpenRevisionAsync(third);
        Assert.Equal(new byte[] { 0, 1, 255 }, Assert.Single(await secondRoot.GetPropertiesAsync(path)).Value.ToArray());
        Assert.Empty(await thirdRoot.GetPropertiesAsync(path));
    }

    [Fact]
    public async Task RevisionPropertyCompareAndSwapIsAtomic() {
        using var directory = new TemporaryDirectory();
        ISvnWritableRepository repository = await SvnFileSystemRepository.CreateAsync(directory.Path);
        var revision = await repository.CommitAsync(new SvnCommitRequest(new SvnRevision(0), RevisionProperties("Original"), []));

        await repository.ChangeRevisionPropertyAsync(new SvnRevisionPropertyChange(revision, "svn:log", "Changed"u8.ToArray(), false, "Original"u8.ToArray()));

        Assert.Equal("Changed", (await repository.GetRevisionPropertiesAsync(revision)).LogMessage);
        await Assert.ThrowsAsync<SvnRevisionPropertyConflictException>(async () => await repository.ChangeRevisionPropertyAsync(new SvnRevisionPropertyChange(revision, "svn:log", "Lost"u8.ToArray(), false, "Original"u8.ToArray())));
    }

    [Fact]
    public async Task LockTokenIsRequiredAndSuccessfulCommitReleasesLock() {
        using var directory = new TemporaryDirectory();
        ISvnWritableRepository repository = await SvnFileSystemRepository.CreateAsync(directory.Path);
        var path = new SvnRepositoryPath("locked.txt");
        var first = await repository.CommitAsync(new SvnCommitRequest(new SvnRevision(0), RevisionProperties("Add"), [SvnCommitChange.AddFile(path, "one"u8)]));
        var fileLock = await repository.LockAsync(new SvnLockRequest(path, "owner", "test", false, first));
        var change = SvnCommitChange.ModifyFile(path, "two"u8);

        await Assert.ThrowsAsync<SvnLockException>(async () => await repository.CommitAsync(new SvnCommitRequest(first, RevisionProperties("No token"), [change])));
        var committed = await repository.CommitAsync(new SvnCommitRequest(first, RevisionProperties("With token"), [change]) { LockTokens = new Dictionary<SvnRepositoryPath, string> { [path] = fileLock.Token } });

        Assert.Equal(2, committed.Value);
        Assert.Null(await repository.GetLockAsync(path));
    }

    [Fact]
    public async Task WriterLockPreventsConcurrentProcessStyleWrite() {
        using var directory = new TemporaryDirectory();
        var repository = await SvnFileSystemRepository.CreateAsync(directory.Path);
        await using var writerLock = new FileStream(System.IO.Path.Combine(directory.Path, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        await Assert.ThrowsAsync<SvnRepositoryBusyException>(async () =>
            await repository.CommitAsync(new SvnCommitRequest(new SvnRevision(0), RevisionProperties("Blocked"), [])));
    }

    [Fact]
    public async Task ReopenRemovesAbandonedTransactions() {
        using var directory = new TemporaryDirectory();
        await SvnFileSystemRepository.CreateAsync(directory.Path);
        var abandoned = System.IO.Path.Combine(directory.Path, "transactions", "abandoned");
        Directory.CreateDirectory(abandoned);
        await File.WriteAllTextAsync(System.IO.Path.Combine(abandoned, "partial"), "data");

        await SvnFileSystemRepository.OpenAsync(directory.Path);

        Assert.False(Directory.Exists(abandoned));
    }

    [Fact]
    public async Task ReopenCompletesPublishedRevisionAfterCurrentMarkerCrash() {
        using var directory = new TemporaryDirectory();
        ISvnWritableRepository repository = await SvnFileSystemRepository.CreateAsync(directory.Path);
        await repository.CommitAsync(new SvnCommitRequest(new SvnRevision(0), RevisionProperties("Recovered"), [SvnCommitChange.AddFile(new SvnRepositoryPath("large.bin"), "body"u8)]));
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, "current"), "0");
        File.Delete(System.IO.Path.Combine(directory.Path, "revprops", "000001.json"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, "journal", "pending.json"), "{\"transactionName\":\"already-moved\",\"revision\":1}");

        var reopened = await SvnFileSystemRepository.OpenAsync(directory.Path);

        Assert.Equal(1, (await reopened.GetLatestRevisionAsync()).Value);
        Assert.Equal("Recovered", (await reopened.GetRevisionPropertiesAsync(new SvnRevision(1))).LogMessage);
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "journal", "pending.json")));
    }

    private static SvnRevisionProperties RevisionProperties(string message) =>
        new("filesystem", DateTimeOffset.Parse("2026-07-10T12:00:00Z"), message, SvnPropertyCollection.Empty);

    private static async Task<string> ReadFileAsync(
        ISvnRepository repository,
        SvnRevision revision,
        SvnRepositoryPath path)
    {
        var root = await repository.OpenRevisionAsync(revision);
        await using var stream = await root.OpenFileAsync(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SvnFlux.FileSystem.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

internal static class AsyncEnumerableTestExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source)
        {
            result.Add(item);
        }

        return result;
    }
}

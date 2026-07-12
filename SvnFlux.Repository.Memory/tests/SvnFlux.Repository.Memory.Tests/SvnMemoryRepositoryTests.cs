using System.Text;
using SvnFlux.Core;

namespace SvnFlux.Repository.Memory.Tests;

public sealed class SvnMemoryRepositoryTests {
    [Fact]
    public async Task CommitCreatesReadableRevisionAndPreservesOldSnapshot() {
        var repository = new SvnMemoryRepository();
        var path = new SvnRepositoryPath("src/readme.txt");
        var first = await repository.CommitAsync(new(new SvnRevision(0), Properties("first"), [SvnCommitChange.AddFile(path, "one"u8)]));
        var second = await repository.CommitAsync(new(first, Properties("second"), [SvnCommitChange.ModifyFile(path, "two"u8)]));

        Assert.Equal("one", await ReadAsync(repository, first, path));
        Assert.Equal("two", await ReadAsync(repository, second, path));
        var root = await repository.OpenRevisionAsync(second);
        Assert.Equal("src", Assert.Single(await root.GetDirectoryAsync(new SvnRepositoryPath("")).ToListAsync()).Name);
    }

    [Fact]
    public async Task CopyDeletePropertiesAndLogArePreserved() {
        var repository = new SvnMemoryRepository();
        var source = new SvnRepositoryPath("trunk/file.txt");
        var first = await repository.CommitAsync(new(new SvnRevision(0), Properties("add"), [
            SvnCommitChange.AddFile(source, "body"u8).WithPropertyChanges([SvnPropertyChange.Set("custom:x", [1, 2])])
        ]));
        var branch = new SvnRepositoryPath("branches/test");
        var second = await repository.CommitAsync(new(first, Properties("copy"), [
            SvnCommitChange.Copy(branch, SvnNodeKind.Directory, new SvnCopySource(new SvnRepositoryPath("trunk"), first))
        ]));
        var third = await repository.CommitAsync(new(second, Properties("delete"), [SvnCommitChange.Delete(new SvnRepositoryPath("trunk"), SvnNodeKind.Directory)]));

        Assert.Equal("body", await ReadAsync(repository, third, new SvnRepositoryPath("branches/test/file.txt")));
        var copied = await (await repository.OpenRevisionAsync(third)).GetPropertiesAsync(new SvnRepositoryPath("branches/test/file.txt"));
        Assert.Equal(new byte[] { 1, 2 }, Assert.Single(copied).Value.ToArray());
        var log = await repository.GetLogAsync(new([branch], third, new SvnRevision(0))).ToListAsync();
        Assert.Contains(log, entry => entry.ChangedPaths.Any(change => change.CopyFromPath?.Value == "trunk"));
    }

    [Fact]
    public async Task OutOfDateCommitDoesNotPublishPartialRevision() {
        var repository = new SvnMemoryRepository();
        await repository.CommitAsync(new(new SvnRevision(0), Properties("first"), []));

        await Assert.ThrowsAsync<SvnOutOfDateException>(async () =>
            await repository.CommitAsync(new(new SvnRevision(0), Properties("stale"), [SvnCommitChange.AddFile(new("lost.txt"), "lost"u8)])));

        Assert.Equal(1, (await repository.GetLatestRevisionAsync()).Value);
        Assert.Null(await (await repository.OpenRevisionAsync(new SvnRevision(1))).GetNodeInfoAsync(new("lost.txt")));
    }

    [Fact]
    public async Task LockTokenIsRequiredAndReleasedByCommit() {
        var repository = new SvnMemoryRepository();
        var path = new SvnRepositoryPath("locked.txt");
        var first = await repository.CommitAsync(new(new SvnRevision(0), Properties("add"), [SvnCommitChange.AddFile(path, "one"u8)]));
        var fileLock = await repository.LockAsync(new(path, "owner", null, false, first));

        await Assert.ThrowsAsync<SvnLockException>(async () => await repository.CommitAsync(new(first, Properties("blocked"), [SvnCommitChange.ModifyFile(path, "two"u8)])));
        var request = new SvnCommitRequest(first, Properties("allowed"), [SvnCommitChange.ModifyFile(path, "two"u8)]) {
            LockTokens = new Dictionary<SvnRepositoryPath, string> { [path] = fileLock.Token }
        };
        await repository.CommitAsync(request);

        Assert.Null(await repository.GetLockAsync(path));
    }

    [Fact]
    public async Task RevisionPropertiesSupportCompareAndSwap() {
        var repository = new SvnMemoryRepository();
        var revision = await repository.CommitAsync(new(new SvnRevision(0), Properties("old"), []));

        await repository.ChangeRevisionPropertyAsync(new(revision, "svn:log", "new"u8.ToArray(), false, "old"u8.ToArray()));

        Assert.Equal("new", (await repository.GetRevisionPropertiesAsync(revision)).LogMessage);
        await Assert.ThrowsAsync<SvnRevisionPropertyConflictException>(async () =>
            await repository.ChangeRevisionPropertyAsync(new(revision, "svn:log", "lost"u8.ToArray(), false, "old"u8.ToArray())));
    }

    private static SvnRevisionProperties Properties(string message) => new("memory", DateTimeOffset.Parse("2026-07-12T00:00:00Z"), message, SvnPropertyCollection.Empty);
    private static async Task<string> ReadAsync(ISvnRepository repository, SvnRevision revision, SvnRepositoryPath path) {
        await using var stream = await (await repository.OpenRevisionAsync(revision)).OpenFileAsync(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}

internal static class AsyncEnumerableExtensions {
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source) {
        var result = new List<T>();
        await foreach (var item in source) { result.Add(item); }
        return result;
    }
}

namespace SvnFlux.Core;

public interface ISvnRepository
{
    Guid Id { get; }

    ValueTask<SvnRevision> GetLatestRevisionAsync(CancellationToken cancellationToken = default);

    ValueTask<ISvnRevisionRoot> OpenRevisionAsync(
        SvnRevision revision,
        CancellationToken cancellationToken = default);

    ValueTask<SvnRevisionProperties> GetRevisionPropertiesAsync(
        SvnRevision revision,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<SvnLogEntry> GetLogAsync(
        SvnLogQuery query,
        CancellationToken cancellationToken = default);

}

public interface ISvnWritableRepository : ISvnRepository {
    ValueTask<SvnRevision> CommitAsync(SvnCommitRequest request, CancellationToken cancellationToken = default);
    ValueTask<SvnLock> RefreshLockAsync(SvnRepositoryPath path, string token, DateTimeOffset? expires, CancellationToken cancellationToken = default);
    ValueTask ChangeRevisionPropertyAsync(SvnRevisionPropertyChange change, CancellationToken cancellationToken = default);
    ValueTask<SvnLock> LockAsync(SvnLockRequest request, CancellationToken cancellationToken = default);
    ValueTask UnlockAsync(SvnRepositoryPath path, string? token, bool breakLock, CancellationToken cancellationToken = default);
    ValueTask<SvnLock?> GetLockAsync(SvnRepositoryPath path, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SvnLock> GetLocksAsync(SvnRepositoryPath path, CancellationToken cancellationToken = default);
}

public sealed record SvnRevisionPropertyChange(SvnRevision Revision, string Name, ReadOnlyMemory<byte>? Value, bool IgnoreExpectedValue = true, ReadOnlyMemory<byte>? ExpectedValue = null);
public sealed record SvnLock(string Token, SvnRepositoryPath Path, string Owner, string? Comment, DateTimeOffset Created, DateTimeOffset? Expires = null);
public sealed record SvnLockRequest(SvnRepositoryPath Path, string Owner, string? Comment, bool StealLock, SvnRevision? CurrentRevision, DateTimeOffset? Expires = null);

public interface ISvnRevisionRoot
{
    SvnRevision Revision { get; }

    ValueTask<SvnNodeInfo?> GetNodeInfoAsync(
        SvnRepositoryPath path,
        CancellationToken cancellationToken = default);

    ValueTask<Stream> OpenFileAsync(
        SvnRepositoryPath path,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<SvnDirectoryEntry> GetDirectoryAsync(
        SvnRepositoryPath path,
        CancellationToken cancellationToken = default);

    ValueTask<SvnPropertyCollection> GetPropertiesAsync(
        SvnRepositoryPath path,
        CancellationToken cancellationToken = default);
}

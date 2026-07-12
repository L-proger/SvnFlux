using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using SvnFlux.Core;
using SvnFlux.Svndiff;

namespace SvnFlux.Http;

internal sealed class SvnHttpTransactionStore {
    private readonly ConcurrentDictionary<(Guid Repository, string Id), SvnHttpTransaction> _transactions = new();

    public async ValueTask<SvnHttpTransaction> CreateAsync(ISvnWritableRepository repository, string? author, SvnHttpOptions options, CancellationToken token) {
        RemoveExpired(options.TransactionIdleTimeout);
        if (_transactions.Count >= options.MaximumActiveTransactions) throw new BadHttpRequestException("The maximum number of active SVN transactions has been reached.", StatusCodes.Status503ServiceUnavailable);
        var id = Guid.NewGuid().ToString("N");
        var transaction = new SvnHttpTransaction(id, repository, await repository.GetLatestRevisionAsync(token).ConfigureAwait(false), author, options.MaximumPutSize);
        if (!_transactions.TryAdd((repository.Id, id), transaction)) throw new InvalidOperationException("Could not allocate a unique transaction identifier.");
        return transaction;
    }

    public SvnHttpTransaction Get(ISvnRepository repository, string id) {
        if (!_transactions.TryGetValue((repository.Id, id), out var transaction)) throw new SvnHttpTransactionNotFoundException();
        transaction.Touch();
        return transaction;
    }

    public async ValueTask RemoveAsync(SvnHttpTransaction transaction) {
        _transactions.TryRemove((transaction.Repository.Id, transaction.Id), out _);
        await transaction.DisposeAsync().ConfigureAwait(false);
    }

    private void RemoveExpired(TimeSpan timeout) {
        var threshold = DateTimeOffset.UtcNow - timeout;
        foreach (var pair in _transactions.Where(pair => pair.Value.LastAccess < threshold).ToArray()) {
            if (_transactions.TryRemove(pair.Key, out var transaction)) _ = transaction.DisposeAsync();
        }
    }
}

internal sealed class SvnHttpTransaction : IAsyncDisposable {
    private readonly Dictionary<string, byte[]?> _revisionProperties = new(StringComparer.Ordinal);
    private readonly Dictionary<SvnRepositoryPath, StagedFile> _files = [];
    private readonly ConcurrentDictionary<SvnRepositoryPath, string> _lockTokens = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly string _directory;
    private readonly List<StructuralChange> _structuralChanges = [];
    private readonly Dictionary<SvnRepositoryPath, List<SvnHttpPropertyChange>> _nodeProperties = [];
    private readonly long _maximumFileSize;
    private bool _closed;

    public SvnHttpTransaction(string id, ISvnWritableRepository repository, SvnRevision baseRevision, string? author, long maximumFileSize) {
        Id = id;
        Repository = repository;
        BaseRevision = baseRevision;
        Author = author;
        _maximumFileSize = maximumFileSize;
        _directory = Path.Combine(Path.GetTempPath(), "svnflux-http", repository.Id.ToString("N"), id);
        Directory.CreateDirectory(_directory);
        LastAccess = DateTimeOffset.UtcNow;
    }

    public string Id { get; }
    public ISvnWritableRepository Repository { get; }
    public SvnRevision BaseRevision { get; }
    public string? Author { get; }
    public DateTimeOffset LastAccess { get; private set; }

    public void Touch() => LastAccess = DateTimeOffset.UtcNow;

    public void AddLockToken(SvnRepositoryPath path, string? header) {
        if (SvnHttpLock.TryReadToken(header, out var token)) _lockTokens[path] = token;
    }

    public async ValueTask<(bool Added, string Checksum)> PutFileAsync(SvnRepositoryPath path, HttpRequest request, CancellationToken token) {
        await _mutex.WaitAsync(token).ConfigureAwait(false);
        try {
            EnsureOpen();
            var baseRevision = ReadBaseRevision(request.Headers["X-SVN-Version-Name"]);
            var transactionRoot = await Repository.OpenRevisionAsync(BaseRevision, token).ConfigureAwait(false);
            var current = await transactionRoot.GetNodeInfoAsync(path, token).ConfigureAwait(false);
            var copy = FindCopy(path);
            var added = (current is null || DeletedInTransaction(path)) && copy is null;
            if (baseRevision is { } expected) {
                if (current is null || current.Kind != SvnNodeKind.File || current.LastChangedRevision.Value > expected.Value)
                    throw new SvnOutOfDateException(expected, current?.LastChangedRevision ?? BaseRevision);
            } else if (!added && copy is null && !_files.ContainsKey(path)) {
                throw new BadHttpRequestException("An existing file PUT requires X-SVN-Version-Name.");
            }

            var sourceRevision = baseRevision ?? BaseRevision;
            await using var source = await OpenSourceAsync(path, sourceRevision, added, copy, token).ConfigureAwait(false);
            var temporaryPath = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".file");
            try {
                await using (var output = new LimitedWriteStream(new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan), _maximumFileSize)) {
                    if (request.ContentType?.StartsWith("application/vnd.svn-svndiff", StringComparison.OrdinalIgnoreCase) == true)
                        await SvnDiffDecoder.ApplyAsync(source, request.Body, output, token).ConfigureAwait(false);
                    else
                        await request.Body.CopyToAsync(output, token).ConfigureAwait(false);
                }
                var checksum = ValidateChecksum(request.Headers["X-SVN-Result-Fulltext-MD5"], temporaryPath);
                if (_files.Remove(path, out var previous)) TryDelete(previous.Path);
                _files[path] = new(temporaryPath, added);
                Touch();
                return (added, checksum);
            } catch {
                TryDelete(temporaryPath);
                throw;
            }
        } finally { _mutex.Release(); }
    }

    public async ValueTask AddDirectoryAsync(SvnRepositoryPath path, CancellationToken token) {
        await _mutex.WaitAsync(token).ConfigureAwait(false);
        try {
            EnsureOpen();
            var root = await Repository.OpenRevisionAsync(BaseRevision, token).ConfigureAwait(false);
            if (await root.GetNodeInfoAsync(path, token).ConfigureAwait(false) is not null || ExistsInTransaction(path))
                throw new InvalidOperationException($"Path '{path}' already exists.");
            _structuralChanges.Add(new(SvnCommitChangeAction.Add, path, SvnNodeKind.Directory, null));
            Touch();
        } finally { _mutex.Release(); }
    }

    public async ValueTask CopyAsync(SvnRepositoryPath sourcePath, SvnRevision sourceRevision, SvnRepositoryPath targetPath, CancellationToken token) {
        await _mutex.WaitAsync(token).ConfigureAwait(false);
        try {
            EnsureOpen();
            var sourceRoot = await Repository.OpenRevisionAsync(sourceRevision, token).ConfigureAwait(false);
            var source = await sourceRoot.GetNodeInfoAsync(sourcePath, token).ConfigureAwait(false) ?? throw new SvnPathNotFoundException(sourcePath);
            var transactionRoot = await Repository.OpenRevisionAsync(BaseRevision, token).ConfigureAwait(false);
            if (await transactionRoot.GetNodeInfoAsync(targetPath, token).ConfigureAwait(false) is not null || ExistsInTransaction(targetPath))
                throw new InvalidOperationException($"Path '{targetPath}' already exists.");
            _structuralChanges.Add(new(SvnCommitChangeAction.Copy, targetPath, source.Kind, new(sourcePath, sourceRevision)));
            Touch();
        } finally { _mutex.Release(); }
    }

    public async ValueTask DeleteAsync(SvnRepositoryPath path, SvnRevision expectedRevision, CancellationToken token) {
        await _mutex.WaitAsync(token).ConfigureAwait(false);
        try {
            EnsureOpen();
            var root = await Repository.OpenRevisionAsync(BaseRevision, token).ConfigureAwait(false);
            var info = await root.GetNodeInfoAsync(path, token).ConfigureAwait(false);
            var pending = _structuralChanges.LastOrDefault(change => change.Path == path && change.Action is SvnCommitChangeAction.Add or SvnCommitChangeAction.Copy);
            if (info is null && pending is null) throw new SvnPathNotFoundException(path);
            if (info is not null && info.LastChangedRevision.Value > expectedRevision.Value) throw new SvnOutOfDateException(expectedRevision, info.LastChangedRevision);
            _structuralChanges.Add(new(SvnCommitChangeAction.Delete, path, info?.Kind ?? pending!.Kind, null));
            Touch();
        } finally { _mutex.Release(); }
    }

    public async ValueTask SetNodePropertiesAsync(SvnRepositoryPath path, IReadOnlyList<SvnHttpPropertyChange> changes, SvnRevision? expectedRevision, CancellationToken token) {
        await _mutex.WaitAsync(token).ConfigureAwait(false);
        try {
            EnsureOpen();
            var root = await Repository.OpenRevisionAsync(BaseRevision, token).ConfigureAwait(false);
            var info = await root.GetNodeInfoAsync(path, token).ConfigureAwait(false);
            if (info is null && !_files.ContainsKey(path) && !ExistsInTransaction(path)) throw new SvnPathNotFoundException(path);
            if (expectedRevision is { } expected && info is not null && info.LastChangedRevision.Value > expected.Value)
                throw new SvnOutOfDateException(expected, info.LastChangedRevision);
            if (!_nodeProperties.TryGetValue(path, out var pending)) _nodeProperties[path] = pending = [];
            foreach (var change in changes) {
                pending.RemoveAll(value => value.Name == change.Name);
                pending.Add(change);
            }
            Touch();
        } finally { _mutex.Release(); }
    }

    public async ValueTask SetRevisionPropertiesAsync(IReadOnlyList<SvnHttpPropertyChange> changes, CancellationToken token) {
        await _mutex.WaitAsync(token).ConfigureAwait(false);
        try {
            EnsureOpen();
            foreach (var change in changes) _revisionProperties[change.Name] = change.Value;
            Touch();
        } finally { _mutex.Release(); }
    }

    public async ValueTask<(SvnRevision Revision, SvnRevisionProperties Properties)> CommitAsync(bool keepLocks, CancellationToken token) {
        await _mutex.WaitAsync(token).ConfigureAwait(false);
        try {
            EnsureOpen();
            var changes = new List<SvnCommitChange>();
            var propertiesUsed = new HashSet<SvnRepositoryPath>();
            foreach (var change in _structuralChanges) {
                var value = change.Action switch {
                    SvnCommitChangeAction.Add => SvnCommitChange.AddDirectory(change.Path),
                    SvnCommitChangeAction.Copy => SvnCommitChange.Copy(change.Path, change.Kind, change.CopyFrom!),
                    SvnCommitChangeAction.Delete => SvnCommitChange.Delete(change.Path, change.Kind),
                    _ => throw new InvalidOperationException()
                };
                if (change.Action is SvnCommitChangeAction.Add or SvnCommitChangeAction.Copy && _nodeProperties.TryGetValue(change.Path, out var nodeProperties)) {
                    value = value.WithPropertyChanges(ToPropertyChanges(nodeProperties));
                    propertiesUsed.Add(change.Path);
                }
                changes.Add(value);
            }
            foreach (var pair in _files.OrderBy(pair => pair.Key.Value, StringComparer.Ordinal)) {
                var value = pair.Value.Added
                    ? SvnCommitChange.AddFileStream(pair.Key, _ => ValueTask.FromResult<Stream>(OpenStaged(pair.Value.Path)))
                    : SvnCommitChange.ModifyFileStream(pair.Key, _ => ValueTask.FromResult<Stream>(OpenStaged(pair.Value.Path)));
                if (_nodeProperties.TryGetValue(pair.Key, out var nodeProperties)) {
                    value = value.WithPropertyChanges(ToPropertyChanges(nodeProperties));
                    propertiesUsed.Add(pair.Key);
                }
                changes.Add(value);
            }
            var root = await Repository.OpenRevisionAsync(BaseRevision, token).ConfigureAwait(false);
            foreach (var pair in _nodeProperties.Where(pair => !propertiesUsed.Contains(pair.Key))) {
                var info = await root.GetNodeInfoAsync(pair.Key, token).ConfigureAwait(false);
                var kind = info?.Kind ?? _structuralChanges.LastOrDefault(change => change.Path == pair.Key)?.Kind ?? throw new SvnPathNotFoundException(pair.Key);
                changes.Add(SvnCommitChange.ModifyProperties(pair.Key, kind, ToPropertyChanges(pair.Value)));
            }
            var properties = RevisionProperties();
            var request = new SvnCommitRequest(BaseRevision, properties, changes) { LockTokens = _lockTokens, KeepLocks = keepLocks };
            var revision = await Repository.CommitAsync(request, token).ConfigureAwait(false);
            _closed = true;
            return (revision, properties);
        } finally { _mutex.Release(); }
    }

    private SvnRevisionProperties RevisionProperties() {
        string? Text(string name) => _revisionProperties.TryGetValue(name, out var value) && value is not null ? System.Text.Encoding.UTF8.GetString(value) : null;
        var custom = _revisionProperties.Where(pair => pair.Key is not ("svn:author" or "svn:date" or "svn:log") && pair.Value is not null)
            .Select(pair => new SvnProperty(pair.Key, pair.Value!));
        return new(Text("svn:author") ?? Author, DateTimeOffset.UtcNow, Text("svn:log"), new(custom));
    }

    private async ValueTask<Stream> OpenSourceAsync(SvnRepositoryPath path, SvnRevision revision, bool added, StructuralChange? copy, CancellationToken token) {
        if (_files.TryGetValue(path, out var staged)) return OpenStaged(staged.Path);
        if (added) return new MemoryStream([], writable: false);
        if (copy is not null) {
            var suffix = path.Value.Length == copy.Path.Value.Length ? "" : path.Value[(copy.Path.Value.Length + 1)..];
            var sourcePath = suffix.Length == 0 ? copy.CopyFrom!.Path : copy.CopyFrom!.Path.Append(new(suffix));
            var copyRoot = await Repository.OpenRevisionAsync(copy.CopyFrom.Revision, token).ConfigureAwait(false);
            return await copyRoot.OpenFileAsync(sourcePath, token).ConfigureAwait(false);
        }
        var root = await Repository.OpenRevisionAsync(revision, token).ConfigureAwait(false);
        return await root.OpenFileAsync(path, token).ConfigureAwait(false);
    }

    private StructuralChange? FindCopy(SvnRepositoryPath path) => _structuralChanges.LastOrDefault(change =>
        change.Action == SvnCommitChangeAction.Copy && (change.Path == path || path.Value.StartsWith(change.Path.Value + "/", StringComparison.Ordinal)));

    private bool DeletedInTransaction(SvnRepositoryPath path) => _structuralChanges.LastOrDefault(change =>
        change.Path == path || path.Value.StartsWith(change.Path.Value + "/", StringComparison.Ordinal))?.Action == SvnCommitChangeAction.Delete;

    private bool ExistsInTransaction(SvnRepositoryPath path) {
        var change = _structuralChanges.LastOrDefault(change => change.Path == path ||
            path.Value.StartsWith(change.Path.Value + "/", StringComparison.Ordinal));
        return change?.Action is SvnCommitChangeAction.Add or SvnCommitChangeAction.Copy;
    }

    private static IReadOnlyList<SvnPropertyChange> ToPropertyChanges(IEnumerable<SvnHttpPropertyChange> changes) =>
        changes.Select(change => change.Value is null ? SvnPropertyChange.Delete(change.Name) : SvnPropertyChange.Set(change.Name, change.Value)).ToArray();

    internal static SvnRevision? ReadBaseRevision(string? value) {
        if (string.IsNullOrEmpty(value)) return null;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0)
            throw new BadHttpRequestException("X-SVN-Version-Name is invalid.");
        return new(revision);
    }

    private static string ValidateChecksum(string? expected, string path) {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(MD5.HashData(stream));
        if (!string.IsNullOrEmpty(expected) && !actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The PUT result checksum does not match the reconstructed file.");
        return actual;
    }

    private static FileStream OpenStaged(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private void EnsureOpen() { if (_closed) throw new InvalidOperationException("The SVN transaction is already closed."); }

    public ValueTask DisposeAsync() {
        _closed = true;
        _mutex.Dispose();
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }

    private sealed record StagedFile(string Path, bool Added);

    private sealed record StructuralChange(SvnCommitChangeAction Action, SvnRepositoryPath Path, SvnNodeKind Kind, SvnCopySource? CopyFrom);

    private sealed class LimitedWriteStream(Stream inner, long maximumLength) : Stream {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) { Ensure(count); inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Ensure(buffer.Length); inner.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Ensure(buffer.Length); return inner.WriteAsync(buffer, cancellationToken); }
        private void Ensure(int count) {
            if (count > maximumLength - _length) throw new BadHttpRequestException("The reconstructed file is too large.", StatusCodes.Status413PayloadTooLarge);
            _length += count;
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync().ConfigureAwait(false); GC.SuppressFinalize(this); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

internal sealed class SvnHttpTransactionNotFoundException : Exception;

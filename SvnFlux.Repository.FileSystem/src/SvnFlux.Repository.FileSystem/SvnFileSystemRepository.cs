using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SvnFlux.Core;

namespace SvnFlux.Repository.FileSystem;

public sealed class SvnFileSystemRepository : ISvnWritableRepository
{
    private readonly string _rootPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private SvnFileSystemRepository(string rootPath, Guid id)
    {
        _rootPath = rootPath;
        Id = id;
    }

    public Guid Id { get; }
    public string RootPath => _rootPath;

    public static async ValueTask<SvnFileSystemRepository> CreateAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ValidateRootPath(rootPath);
        if (Directory.Exists(fullPath) && Directory.EnumerateFileSystemEntries(fullPath).Any())
        {
            throw new IOException($"Repository directory '{fullPath}' is not empty.");
        }

        Directory.CreateDirectory(fullPath);
        Directory.CreateDirectory(Path.Combine(fullPath, "revisions"));
        Directory.CreateDirectory(Path.Combine(fullPath, "revprops"));
        Directory.CreateDirectory(Path.Combine(fullPath, "transactions"));
        Directory.CreateDirectory(Path.Combine(fullPath, "locks"));
        Directory.CreateDirectory(Path.Combine(fullPath, "journal"));

        var id = Guid.NewGuid();
        await WriteJsonAsync(
            Path.Combine(fullPath, "format.json"),
            new FormatDocument(StorageModels.FormatName, StorageModels.FormatVersion, "hard-links"),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(fullPath, "uuid"), id.ToString(), cancellationToken).ConfigureAwait(false);

        var revision = new SvnRevision(0);
        var revisionPath = GetRevisionPath(fullPath, revision);
        Directory.CreateDirectory(Path.Combine(revisionPath, "tree"));
        await WriteJsonAsync(
            Path.Combine(revisionPath, "manifest.json"),
            new ManifestDocument
            {
                Nodes =
                [
                    new NodeDocument
                    {
                        Path = string.Empty,
                        Kind = "directory",
                        LastChangedRevision = 0
                    }
                ]
            },
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(Path.Combine(revisionPath, "changes.json"), new ChangesDocument(), cancellationToken).ConfigureAwait(false);
        var revisionProperties = new RevisionPropertiesDocument { Date = DateTimeOffset.UtcNow };
        await WriteJsonAsync(Path.Combine(revisionPath, "metadata.json"), revisionProperties, cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(GetRevisionPropertiesPath(fullPath, revision), revisionProperties, cancellationToken).ConfigureAwait(false);
        await WriteCurrentAsync(fullPath, revision, cancellationToken).ConfigureAwait(false);
        return new SvnFileSystemRepository(fullPath, id);
    }

    public static async ValueTask<SvnFileSystemRepository> OpenAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = ValidateRootPath(rootPath);
        var format = await ReadJsonAsync<FormatDocument>(Path.Combine(fullPath, "format.json"), cancellationToken).ConfigureAwait(false);
        if (format.Name != StorageModels.FormatName || format.Version != StorageModels.FormatVersion)
        {
            throw new NotSupportedException($"Unsupported filesystem repository format '{format.Name}' version {format.Version}.");
        }

        if (!string.Equals(format.LinkMode, "hard-links", StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unsupported filesystem repository link mode '{format.LinkMode}'.");
        }

        var uuidText = await File.ReadAllTextAsync(Path.Combine(fullPath, "uuid"), cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParse(uuidText.Trim(), out var id))
        {
            throw new InvalidDataException("The repository UUID is invalid.");
        }

        await RecoverAsync(fullPath, cancellationToken).ConfigureAwait(false);
        _ = await ReadCurrentAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return new SvnFileSystemRepository(fullPath, id);
    }

    public async ValueTask<SvnRevision> CreateRevisionAsync(
        IEnumerable<SvnFileSystemChange> changes,
        SvnRevisionProperties revisionProperties,
        CancellationToken cancellationToken = default,
        SvnRevision? expectedBaseRevision = null)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(revisionProperties);
        var changeArray = changes.ToArray();
        if (changeArray.Select(change => change.Path).Distinct().Count() != changeArray.Length)
        {
            throw new ArgumentException("A commit cannot contain duplicate paths.", nameof(changes));
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var writerLock = AcquireWriterLock();
            var baseRevision = await GetLatestRevisionAsync(cancellationToken).ConfigureAwait(false);
            if (expectedBaseRevision is { } expected && expected != baseRevision) { throw new SvnOutOfDateException(expected, baseRevision); }
            var nextRevision = new SvnRevision(checked(baseRevision.Value + 1));
            var baseManifest = await ReadManifestAsync(baseRevision, cancellationToken).ConfigureAwait(false);
            var nodes = baseManifest.Nodes.ToDictionary(node => node.Path, node => node.Clone(), StringComparer.Ordinal);
            var copySourceManifests = new Dictionary<SvnRevision, ManifestDocument>();
            foreach (var sourceRevision in changeArray.Where(change => change.CopyFrom is not null).Select(change => change.CopyFrom!.Revision).Distinct()) {
                copySourceManifests[sourceRevision] = sourceRevision == baseRevision ? baseManifest : await ReadManifestAsync(sourceRevision, cancellationToken).ConfigureAwait(false);
            }
            var changeRecords = new List<ChangeDocument>();

            foreach (var change in changeArray)
            {
                ApplyChange(nodes, change, nextRevision, changeRecords, copySourceManifests);
            }

            var transactionName = Guid.NewGuid().ToString("N");
            var transactionPath = Path.Combine(_rootPath, "transactions", transactionName);
            var temporaryTreePath = Path.Combine(transactionPath, "tree");
            var finalRevisionPath = GetRevisionPath(_rootPath, nextRevision);
            Directory.CreateDirectory(temporaryTreePath);
            try
            {
                foreach (var directory in nodes.Values.Where(node => node.Kind == "directory").OrderBy(node => PathDepth(node.Path)))
                {
                    Directory.CreateDirectory(GetPhysicalPath(temporaryTreePath, new SvnRepositoryPath(directory.Path)));
                }

                foreach (var file in nodes.Values.Where(node => node.Kind == "file").OrderBy(node => node.Path, StringComparer.Ordinal))
                {
                    var logicalPath = new SvnRepositoryPath(file.Path);
                    var temporaryFilePath = GetPhysicalPath(temporaryTreePath, logicalPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(temporaryFilePath)!);
                    if (file.BodyRevision == nextRevision.Value)
                    {
                        var change = changeArray.Single(item => item.Path == logicalPath && !item.IsDelete);
                        await using var source = await change.OpenContentAsync(cancellationToken).ConfigureAwait(false);
                        await using var destination = new FileStream(temporaryFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await source.CopyToAsync(destination, 128 * 1024, cancellationToken).ConfigureAwait(false);
                        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var bodyRevision = new SvnRevision(file.BodyRevision ?? throw new InvalidDataException("A file node has no body revision."));
                        var bodyPath = GetPhysicalPath(GetRevisionTreePath(_rootPath, bodyRevision), new SvnRepositoryPath(file.BodyPath ?? file.Path));
                        FileLink.CreateHardLink(temporaryFilePath, bodyPath);
                    }
                }

                var manifest = new ManifestDocument
                {
                    Nodes = nodes.Values.OrderBy(node => node.Path, StringComparer.Ordinal).ToList()
                };
                var propertiesDocument = RevisionPropertiesDocument.FromCore(revisionProperties);
                await WriteJsonAsync(Path.Combine(transactionPath, "manifest.json"), manifest, cancellationToken).ConfigureAwait(false);
                await WriteJsonAsync(Path.Combine(transactionPath, "changes.json"), new ChangesDocument { Changes = changeRecords }, cancellationToken).ConfigureAwait(false);
                await WriteJsonAsync(Path.Combine(transactionPath, "metadata.json"), propertiesDocument, cancellationToken).ConfigureAwait(false);
                await WriteJsonAsync(Path.Combine(transactionPath, "revprops.json"), propertiesDocument, cancellationToken).ConfigureAwait(false);

                var pendingPath = Path.Combine(_rootPath, "journal", "pending.json");
                await WriteJsonAsync(pendingPath, new PendingPublicationDocument(transactionName, nextRevision.Value), cancellationToken).ConfigureAwait(false);
                Directory.Move(transactionPath, finalRevisionPath);
                await PublishRevisionPropertiesAsync(_rootPath, nextRevision, propertiesDocument, cancellationToken).ConfigureAwait(false);
                await WriteCurrentAsync(_rootPath, nextRevision, cancellationToken).ConfigureAwait(false);
                File.Delete(pendingPath);
                return nextRevision;
            }
            catch
            {
                if (Directory.Exists(transactionPath))
                {
                    Directory.Delete(transactionPath, recursive: true);
                }

                throw;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<SvnRevision> CommitAsync(SvnCommitRequest request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var change in request.Changes) {
            await foreach (var fileLock in GetLocksAsync(change.Path, cancellationToken).ConfigureAwait(false)) {
                if (fileLock.Path != change.Path && !(change.Action == SvnCommitChangeAction.Delete && change.NodeKind == SvnNodeKind.Directory)) continue;
                if (!request.LockTokens.TryGetValue(fileLock.Path, out var token) || token != fileLock.Token)
                    throw new SvnLockException(fileLock.Path, "a matching lock token is required for this commit.");
            }
        }
        var changes = request.Changes.Select(change => {
            var result = change.Action switch {
                SvnCommitChangeAction.Delete => SvnFileSystemChange.Delete(change.Path),
                SvnCommitChangeAction.Copy => SvnFileSystemChange.Copy(change.Path, change.NodeKind, change.CopyFrom ?? throw new InvalidDataException("A copy change has no source.")),
                SvnCommitChangeAction.Modify when change.PropertyChanges.Count != 0 && !change.HasContent => SvnFileSystemChange.ModifyProperties(change.Path, change.NodeKind, change.PropertyChanges),
                _ when change.NodeKind == SvnNodeKind.Directory => SvnFileSystemChange.AddDirectory(change.Path),
                _ => SvnFileSystemChange.WriteStream(change.Path, token => change.OpenContentAsync(token))
            };
            return change.PropertyChanges.Count == 0 ? result : result.WithPropertyChanges(change.PropertyChanges);
        });
        var revision = await CreateRevisionAsync(changes, request.RevisionProperties, cancellationToken, request.BaseRevision).ConfigureAwait(false);
        if (!request.KeepLocks) {
            foreach (var pair in request.LockTokens) { if (await GetLockAsync(pair.Key, cancellationToken).ConfigureAwait(false) is { Token: var token } && token == pair.Value) { await UnlockAsync(pair.Key, token, false, cancellationToken).ConfigureAwait(false); } }
        }
        return revision;
    }

    public async ValueTask<SvnLock> LockAsync(SvnLockRequest request, CancellationToken cancellationToken = default) {
        if (request.Path.IsRoot) { throw new SvnLockException(request.Path, "the repository root cannot be locked."); }
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            await using var writerLock = AcquireWriterLock();
            var latest = await GetLatestRevisionAsync(cancellationToken).ConfigureAwait(false);
            var root = await OpenRevisionAsync(latest, cancellationToken).ConfigureAwait(false);
            var info = await root.GetNodeInfoAsync(request.Path, cancellationToken).ConfigureAwait(false) ?? throw new SvnPathNotFoundException(request.Path);
            if (info.Kind != SvnNodeKind.File) { throw new SvnLockException(request.Path, "only files can be locked."); }
            if (request.CurrentRevision is { } current && info.LastChangedRevision.Value > current.Value) { throw new SvnOutOfDateException(current, info.LastChangedRevision); }
            var lockPath = GetLockPath(request.Path);
            var existing = await ReadLockAsync(lockPath, cancellationToken).ConfigureAwait(false);
            if (existing is not null && !request.StealLock) { throw new SvnLockException(request.Path, "the path is already locked."); }
            var value = new SvnLock("opaquelocktoken:" + Guid.NewGuid(), request.Path, request.Owner, request.Comment, DateTimeOffset.UtcNow, request.Expires);
            var temporaryPath = lockPath + ".tmp-" + Guid.NewGuid().ToString("N");
            await WriteJsonAsync(temporaryPath, ToDocument(value), cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            File.Move(temporaryPath, lockPath, overwrite: true);
            return value;
        }
        finally { _writeGate.Release(); }
    }

    public async ValueTask<SvnLock> RefreshLockAsync(SvnRepositoryPath path, string token, DateTimeOffset? expires, CancellationToken cancellationToken = default) {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            await using var writerLock = AcquireWriterLock();
            var lockPath = GetLockPath(path);
            var current = await ReadLockAsync(lockPath, cancellationToken).ConfigureAwait(false) ?? throw new SvnLockException(path, "the path is not locked.");
            if (current.Token != token) throw new SvnLockException(path, "the lock token does not match.");
            var refreshed = current with { Expires = expires };
            var temporaryPath = lockPath + ".tmp-" + Guid.NewGuid().ToString("N");
            await WriteJsonAsync(temporaryPath, ToDocument(refreshed), cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, lockPath, overwrite: true);
            return refreshed;
        }
        finally { _writeGate.Release(); }
    }

    public async ValueTask UnlockAsync(SvnRepositoryPath path, string? token, bool breakLock, CancellationToken cancellationToken = default) {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            await using var writerLock = AcquireWriterLock();
            var lockPath = GetLockPath(path);
            if (!File.Exists(lockPath)) { throw new SvnLockException(path, "the path is not locked."); }
            var current = await ReadLockAsync(lockPath, cancellationToken).ConfigureAwait(false) ?? throw new SvnLockException(path, "the path is not locked.");
            if (!breakLock && !string.Equals(token, current.Token, StringComparison.Ordinal)) { throw new SvnLockException(path, "the lock token does not match."); }
            File.Delete(lockPath);
        }
        finally { _writeGate.Release(); }
    }

    public async ValueTask<SvnLock?> GetLockAsync(SvnRepositoryPath path, CancellationToken cancellationToken = default) {
        var lockPath = GetLockPath(path);
        return await ReadLockAsync(lockPath, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<SvnLock> GetLocksAsync(SvnRepositoryPath path, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        var locksPath = Path.Combine(_rootPath, "locks");
        foreach (var file in Directory.EnumerateFiles(locksPath, "*.json", SearchOption.AllDirectories)) {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await ReadLockAsync(file, cancellationToken).ConfigureAwait(false);
            if (value is not null && (path.IsRoot || value.Path == path || value.Path.Value.StartsWith(path.Value + "/", StringComparison.Ordinal))) { yield return value; }
        }
    }

    public async ValueTask ChangeRevisionPropertyAsync(SvnRevisionPropertyChange change, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(change.Name);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            await using var writerLock = AcquireWriterLock();
            var path = GetRevisionPropertiesPath(_rootPath, change.Revision);
            if (!File.Exists(path)) { throw new SvnInvalidRevisionException(change.Revision); }
            var document = await ReadJsonAsync<RevisionPropertiesDocument>(path, cancellationToken).ConfigureAwait(false);
            var current = GetRevisionProperty(document, change.Name);
            if (!change.IgnoreExpectedValue && !NullableBytesEqual(current, change.ExpectedValue)) { throw new SvnRevisionPropertyConflictException(change.Revision, change.Name); }
            document = SetRevisionProperty(document, change.Name, change.Value);
            var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            await WriteJsonAsync(temporaryPath, document, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { _writeGate.Release(); }
    }

    public ValueTask<SvnRevision> GetLatestRevisionAsync(CancellationToken cancellationToken = default) =>
        ReadCurrentAsync(_rootPath, cancellationToken);

    public async ValueTask<ISvnRevisionRoot> OpenRevisionAsync(
        SvnRevision revision,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifestAsync(revision, cancellationToken).ConfigureAwait(false);
        return new SvnFileSystemRevisionRoot(this, revision, manifest);
    }

    public async ValueTask<SvnRevisionProperties> GetRevisionPropertiesAsync(
        SvnRevision revision,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(GetRevisionPath(_rootPath, revision)))
        {
            throw new SvnInvalidRevisionException(revision);
        }

        var document = await ReadJsonAsync<RevisionPropertiesDocument>(
            GetRevisionPropertiesPath(_rootPath, revision),
            cancellationToken).ConfigureAwait(false);
        return document.ToCore();
    }

    public async IAsyncEnumerable<SvnLogEntry> GetLogAsync(
        SvnLogQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var step = query.StartRevision.Value >= query.EndRevision.Value ? -1L : 1L;
        var yielded = 0;
        for (var value = query.StartRevision.Value;
             step < 0 ? value >= query.EndRevision.Value : value <= query.EndRevision.Value;
             value += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = new SvnRevision(value);
            if (!Directory.Exists(GetRevisionPath(_rootPath, revision)))
            {
                throw new SvnInvalidRevisionException(revision);
            }

            var changesDocument = await ReadJsonAsync<ChangesDocument>(
                Path.Combine(GetRevisionPath(_rootPath, revision), "changes.json"),
                cancellationToken).ConfigureAwait(false);
            if (revision.Value == 0 && changesDocument.Changes.Count == 0)
            {
                continue;
            }

            var changes = changesDocument.Changes.Select(ToCoreChange).ToArray();
            if (query.Paths.Count != 0 && !changes.Any(change => query.Paths.Any(path => PathMatches(path, change.Path))))
            {
                continue;
            }

            var properties = await GetRevisionPropertiesAsync(revision, cancellationToken).ConfigureAwait(false);
            yield return new SvnLogEntry(revision, properties, changes);
            yielded++;
            if (query.Limit > 0 && yielded >= query.Limit)
            {
                yield break;
            }
        }
    }

    internal string GetTreePath(SvnRevision revision) => GetRevisionTreePath(_rootPath, revision);

    internal async ValueTask<SvnRevisionProperties> GetNodeRevisionPropertiesAsync(
        long revision,
        CancellationToken cancellationToken) =>
        await GetRevisionPropertiesAsync(new SvnRevision(revision), cancellationToken).ConfigureAwait(false);

    private async ValueTask<ManifestDocument> ReadManifestAsync(SvnRevision revision, CancellationToken cancellationToken)
    {
        var revisionPath = GetRevisionPath(_rootPath, revision);
        if (!Directory.Exists(revisionPath))
        {
            throw new SvnInvalidRevisionException(revision);
        }

        var manifest = await ReadJsonAsync<ManifestDocument>(Path.Combine(revisionPath, "manifest.json"), cancellationToken).ConfigureAwait(false);
        if (manifest.Version != StorageModels.FormatVersion)
        {
            throw new NotSupportedException($"Unsupported manifest version {manifest.Version}.");
        }

        return manifest;
    }

    private static void ApplyChange(
        Dictionary<string, NodeDocument> nodes,
        SvnFileSystemChange change,
        SvnRevision revision,
        List<ChangeDocument> changes,
        IReadOnlyDictionary<SvnRevision, ManifestDocument> copySourceManifests)
    {
        if (change.IsDelete) {
            if (!nodes.TryGetValue(change.Path.Value, out var deletedNode)) {
                throw new SvnPathNotFoundException(change.Path);
            }

            foreach (var path in nodes.Keys.Where(path => path == change.Path.Value || path.StartsWith(change.Path.Value + "/", StringComparison.Ordinal)).ToArray())
            {
                nodes.Remove(path);
            }

            changes.Add(new ChangeDocument {
                Path = change.Path.Value,
                Action = "delete",
                Kind = deletedNode.Kind,
                TextModified = deletedNode.Kind == "file"
            });
            TouchParentDirectories(nodes, change.Path, revision);
            return;
        }

        if (change.CopyFrom is { } copyFrom) {
            if (nodes.ContainsKey(change.Path.Value)) { throw new InvalidOperationException($"Repository path '{change.Path}' already exists."); }
            var sourceManifest = copySourceManifests.TryGetValue(copyFrom.Revision, out var manifest) ? manifest : throw new SvnInvalidRevisionException(copyFrom.Revision);
            var sourceNodes = sourceManifest.Nodes.Where(node => node.Path == copyFrom.Path.Value || node.Path.StartsWith(copyFrom.Path.Value + "/", StringComparison.Ordinal)).ToArray();
            if (sourceNodes.Length == 0) { throw new SvnPathNotFoundException(copyFrom.Path); }
            EnsureParentDirectories(nodes, change.Path, revision);
            foreach (var source in sourceNodes) {
                var suffix = source.Path[copyFrom.Path.Value.Length..].TrimStart('/');
                var targetPath = suffix.Length == 0 ? change.Path.Value : change.Path.Value + "/" + suffix;
                nodes[targetPath] = new NodeDocument {
                    Path = targetPath,
                    Kind = source.Kind,
                    BodyRevision = source.BodyRevision,
                    BodyPath = source.Kind == "file" ? source.BodyPath ?? source.Path : null,
                    LastChangedRevision = revision.Value,
                    CopyFromPath = suffix.Length == 0 ? copyFrom.Path.Value : null,
                    CopyFromRevision = suffix.Length == 0 ? copyFrom.Revision.Value : null,
                    Properties = new Dictionary<string, string>(source.Properties, StringComparer.Ordinal)
                };
            }
            ApplyPropertyChanges(nodes[change.Path.Value], change.PropertyChanges);
            changes.Add(new ChangeDocument {
                Path = change.Path.Value,
                Action = "add",
                Kind = change.IsDirectory ? "directory" : "file",
                CopyFromPath = copyFrom.Path.Value,
                CopyFromRevision = copyFrom.Revision.Value,
                PropertiesModified = change.PropertyChanges.Count != 0
            });
            TouchParentDirectories(nodes, change.Path, revision);
            return;
        }

        if (change.IsDirectory) {
            if (nodes.TryGetValue(change.Path.Value, out var existingDirectory)) {
                if (change.PropertyChanges.Count == 0 || existingDirectory.Kind != "directory") { throw new InvalidOperationException($"Repository path '{change.Path}' already exists."); }
                ApplyPropertyChanges(existingDirectory, change.PropertyChanges);
                existingDirectory.LastChangedRevision = revision.Value;
                changes.Add(new ChangeDocument { Path = change.Path.Value, Action = "modify", Kind = "directory", PropertiesModified = true });
                TouchParentDirectories(nodes, change.Path, revision);
                return;
            }
            EnsureParentDirectories(nodes, change.Path, revision);
            nodes[change.Path.Value] = new NodeDocument { Path = change.Path.Value, Kind = "directory", LastChangedRevision = revision.Value };
            ApplyPropertyChanges(nodes[change.Path.Value], change.PropertyChanges);
            changes.Add(new ChangeDocument { Path = change.Path.Value, Action = "add", Kind = "directory" });
            TouchParentDirectories(nodes, change.Path, revision);
            return;
        }

        if (!change.HasContent && nodes.TryGetValue(change.Path.Value, out var propertyNode)) {
            ApplyPropertyChanges(propertyNode, change.PropertyChanges);
            propertyNode.LastChangedRevision = revision.Value;
            changes.Add(new ChangeDocument { Path = change.Path.Value, Action = "modify", Kind = propertyNode.Kind, PropertiesModified = true });
            TouchParentDirectories(nodes, change.Path, revision);
            return;
        }

        var existed = nodes.TryGetValue(change.Path.Value, out var existing);
        EnsureParentDirectories(nodes, change.Path, revision);
        nodes[change.Path.Value] = new NodeDocument
        {
            Path = change.Path.Value,
            Kind = "file",
            BodyRevision = revision.Value,
            BodyPath = change.Path.Value,
            LastChangedRevision = revision.Value,
            Properties = existing is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(existing.Properties, StringComparer.Ordinal)
        };
        ApplyPropertyChanges(nodes[change.Path.Value], change.PropertyChanges);
        changes.Add(new ChangeDocument
        {
            Path = change.Path.Value,
            Action = existed ? "modify" : "add",
            Kind = "file",
            TextModified = true,
            PropertiesModified = change.PropertyChanges.Count != 0
        });
        TouchParentDirectories(nodes, change.Path, revision);
    }

    private static void ApplyPropertyChanges(NodeDocument node, IReadOnlyList<SvnPropertyChange> changes) {
        foreach (var change in changes) {
            if (change.Value is { } value) { node.Properties[change.Name] = Convert.ToBase64String(value.Span); }
            else { node.Properties.Remove(change.Name); }
        }
    }

    private static void EnsureParentDirectories(
        Dictionary<string, NodeDocument> nodes,
        SvnRepositoryPath path,
        SvnRevision revision)
    {
        var segments = path.Value.Split('/');
        for (var count = 1; count < segments.Length; count++)
        {
            var directoryPath = string.Join('/', segments.AsSpan(0, count).ToArray());
            if (nodes.TryGetValue(directoryPath, out var existing))
            {
                if (existing.Kind != "directory")
                {
                    throw new SvnNodeKindMismatchException(new SvnRepositoryPath(directoryPath), SvnNodeKind.Directory);
                }

                continue;
            }

            nodes[directoryPath] = new NodeDocument
            {
                Path = directoryPath,
                Kind = "directory",
                LastChangedRevision = revision.Value
            };
        }
    }

    private static void TouchParentDirectories(
        Dictionary<string, NodeDocument> nodes,
        SvnRepositoryPath path,
        SvnRevision revision)
    {
        nodes[string.Empty].LastChangedRevision = revision.Value;
        var segments = path.Value.Split('/');
        for (var count = 1; count < segments.Length; count++)
        {
            nodes[string.Join('/', segments.AsSpan(0, count).ToArray())].LastChangedRevision = revision.Value;
        }
    }

    private static SvnChangedPath ToCoreChange(ChangeDocument change) => new(
        new SvnRepositoryPath(change.Path),
        change.Action switch
        {
            "add" => SvnChangeAction.Add,
            "delete" => SvnChangeAction.Delete,
            "replace" => SvnChangeAction.Replace,
            _ => SvnChangeAction.Modify
        },
        change.Kind == "directory" ? SvnNodeKind.Directory : SvnNodeKind.File,
        change.TextModified,
        change.PropertiesModified,
        change.CopyFromPath is null ? null : new SvnRepositoryPath(change.CopyFromPath),
        change.CopyFromRevision is null ? null : new SvnRevision(change.CopyFromRevision.Value));

    private static bool PathMatches(SvnRepositoryPath queryPath, SvnRepositoryPath changedPath) =>
        queryPath.IsRoot || changedPath.Value == queryPath.Value || changedPath.Value.StartsWith(queryPath.Value + "/", StringComparison.Ordinal);

    private static int PathDepth(string path) => path.Count(character => character == '/');
    private static ReadOnlyMemory<byte>? GetRevisionProperty(RevisionPropertiesDocument document, string name) => name switch {
        "svn:author" => document.Author is null ? null : System.Text.Encoding.UTF8.GetBytes(document.Author),
        "svn:date" => document.Date is null ? null : System.Text.Encoding.UTF8.GetBytes(document.Date.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)),
        "svn:log" => document.LogMessage is null ? null : System.Text.Encoding.UTF8.GetBytes(document.LogMessage),
        _ => document.CustomProperties.TryGetValue(name, out var value) ? Convert.FromBase64String(value) : null
    };
    private static RevisionPropertiesDocument SetRevisionProperty(RevisionPropertiesDocument document, string name, ReadOnlyMemory<byte>? value) {
        var custom = new Dictionary<string, string>(document.CustomProperties, StringComparer.Ordinal);
        if (name is not "svn:author" and not "svn:date" and not "svn:log") {
            if (value is { } bytes) { custom[name] = Convert.ToBase64String(bytes.Span); } else { custom.Remove(name); }
        }
        return new RevisionPropertiesDocument {
            Author = name == "svn:author" ? value is { } author ? System.Text.Encoding.UTF8.GetString(author.Span) : null : document.Author,
            Date = name == "svn:date" ? value is { } date ? DateTimeOffset.Parse(System.Text.Encoding.UTF8.GetString(date.Span), CultureInfo.InvariantCulture) : null : document.Date,
            LogMessage = name == "svn:log" ? value is { } log ? System.Text.Encoding.UTF8.GetString(log.Span) : null : document.LogMessage,
            CustomProperties = custom
        };
    }
    private static bool NullableBytesEqual(ReadOnlyMemory<byte>? left, ReadOnlyMemory<byte>? right) => left is null ? right is null : right is { } value && left.Value.Span.SequenceEqual(value.Span);
    private static string ValidateRootPath(string rootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        return Path.GetFullPath(rootPath);
    }

    private static string GetRevisionPath(string rootPath, SvnRevision revision) =>
        Path.Combine(rootPath, "revisions", revision.Value.ToString("D6", CultureInfo.InvariantCulture));

    private static string GetRevisionTreePath(string rootPath, SvnRevision revision) =>
        Path.Combine(GetRevisionPath(rootPath, revision), "tree");

    private static string GetRevisionPropertiesPath(string rootPath, SvnRevision revision) =>
        Path.Combine(rootPath, "revprops", revision.Value.ToString("D6", CultureInfo.InvariantCulture) + ".json");

    private static async ValueTask<SvnLock?> ReadLockAsync(string path, CancellationToken cancellationToken) {
        if (!File.Exists(path)) return null;
        SvnLock value;
        try { value = ToCore(await ReadJsonAsync<LockDocument>(path, cancellationToken).ConfigureAwait(false)); }
        catch (FileNotFoundException) { return null; }
        if (value.Expires is not { } expires || expires > DateTimeOffset.UtcNow) return value;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return null;
    }

    private string GetLockPath(SvnRepositoryPath path) => GetPhysicalPath(Path.Combine(_rootPath, "locks"), path) + ".json";
    private static LockDocument ToDocument(SvnLock value) => new() { Token = value.Token, Path = value.Path.Value, Owner = value.Owner, Comment = value.Comment, Created = value.Created, Expires = value.Expires };
    private static SvnLock ToCore(LockDocument value) => new(value.Token, new SvnRepositoryPath(value.Path), value.Owner, value.Comment, value.Created, value.Expires);

    internal static string GetPhysicalPath(string treePath, SvnRepositoryPath path)
    {
        var result = treePath;
        if (!path.IsRoot)
        {
            foreach (var segment in path.Value.Split('/'))
            {
                result = Path.Combine(result, segment);
            }
        }

        var fullTreePath = Path.GetFullPath(treePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullResult = Path.GetFullPath(result);
        if (!fullResult.StartsWith(fullTreePath, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullResult.TrimEnd(Path.DirectorySeparatorChar), fullTreePath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The repository path escapes the revision tree.", nameof(path));
        }

        return fullResult;
    }

    private static async ValueTask<SvnRevision> ReadCurrentAsync(string rootPath, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(Path.Combine(rootPath, "current"), cancellationToken).ConfigureAwait(false);
        return long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? new SvnRevision(value)
            : throw new InvalidDataException("The youngest revision marker is invalid.");
    }

    private static async ValueTask WriteCurrentAsync(string rootPath, SvnRevision revision, CancellationToken cancellationToken)
    {
        var currentPath = Path.Combine(rootPath, "current");
        var temporaryPath = currentPath + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporaryPath, revision.Value.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, currentPath, overwrite: true);
    }

    private FileStream AcquireWriterLock() {
        try { return new FileStream(Path.Combine(_rootPath, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.WriteThrough); }
        catch (IOException) { throw new SvnRepositoryBusyException(); }
    }

    private static async ValueTask RecoverAsync(string rootPath, CancellationToken cancellationToken) {
        var lockPath = Path.Combine(rootPath, "write.lock");
        await using var writerLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.WriteThrough);
        var current = await ReadCurrentAsync(rootPath, cancellationToken).ConfigureAwait(false);
        var pendingPath = Path.Combine(rootPath, "journal", "pending.json");
        if (File.Exists(pendingPath)) {
            var pending = await ReadJsonAsync<PendingPublicationDocument>(pendingPath, cancellationToken).ConfigureAwait(false);
            var revision = new SvnRevision(pending.Revision);
            var transactionPath = Path.Combine(rootPath, "transactions", pending.TransactionName);
            var revisionPath = GetRevisionPath(rootPath, revision);
            if (Directory.Exists(revisionPath)) {
                var propertiesPath = Path.Combine(revisionPath, "revprops.json");
                var properties = File.Exists(propertiesPath)
                    ? await ReadJsonAsync<RevisionPropertiesDocument>(propertiesPath, cancellationToken).ConfigureAwait(false)
                    : await ReadJsonAsync<RevisionPropertiesDocument>(Path.Combine(revisionPath, "metadata.json"), cancellationToken).ConfigureAwait(false);
                await PublishRevisionPropertiesAsync(rootPath, revision, properties, cancellationToken).ConfigureAwait(false);
                if (current.Value < revision.Value) { await WriteCurrentAsync(rootPath, revision, cancellationToken).ConfigureAwait(false); current = revision; }
            }
            else if (Directory.Exists(transactionPath)) { Directory.Delete(transactionPath, recursive: true); }
            File.Delete(pendingPath);
        }
        foreach (var transaction in Directory.EnumerateDirectories(Path.Combine(rootPath, "transactions"))) { Directory.Delete(transaction, recursive: true); }
        for (var value = 0L; value <= current.Value; value++) {
            if (!Directory.Exists(GetRevisionPath(rootPath, new SvnRevision(value)))) { throw new InvalidDataException($"Published revision {value} is missing."); }
        }
    }

    private static async ValueTask PublishRevisionPropertiesAsync(string rootPath, SvnRevision revision, RevisionPropertiesDocument properties, CancellationToken cancellationToken) {
        var path = GetRevisionPropertiesPath(rootPath, revision);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await WriteJsonAsync(temporaryPath, properties, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static async ValueTask WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, value, StorageModels.JsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<T>(stream, StorageModels.JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"File '{path}' does not contain a valid {typeof(T).Name} document.");
    }
}

using System.Runtime.CompilerServices;
using System.Text;
using SvnFlux.Core;

namespace SvnFlux.Repository.Memory;

public sealed class SvnMemoryRepository : ISvnWritableRepository {
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lockGate = new();
    private Snapshot[] _snapshots;
    private readonly Dictionary<SvnRepositoryPath, SvnLock> _locks = [];

    public SvnMemoryRepository(Guid? id = null) {
        Id = id ?? Guid.NewGuid();
        _snapshots = [new(new SvnRevision(0), SvnRevisionProperties.Empty,
            new Dictionary<string, Node>(StringComparer.Ordinal) { [""] = new(SvnNodeKind.Directory, null, [], new SvnRevision(0)) }, [])];
    }

    public Guid Id { get; }
    public ValueTask<SvnRevision> GetLatestRevisionAsync(CancellationToken token = default) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Snapshots[^1].Revision); }
    public ValueTask<ISvnRevisionRoot> OpenRevisionAsync(SvnRevision revision, CancellationToken token = default) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<ISvnRevisionRoot>(new Root(this, Get(revision))); }
    public ValueTask<SvnRevisionProperties> GetRevisionPropertiesAsync(SvnRevision revision, CancellationToken token = default) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Get(revision).Properties); }

    public async IAsyncEnumerable<SvnLogEntry> GetLogAsync(SvnLogQuery query, [EnumeratorCancellation] CancellationToken token = default) {
        var snapshots = Snapshots;
        Check(query.StartRevision, snapshots); Check(query.EndRevision, snapshots);
        var step = query.StartRevision.Value >= query.EndRevision.Value ? -1L : 1L;
        var count = 0;
        for (var value = query.StartRevision.Value; step < 0 ? value >= query.EndRevision.Value : value <= query.EndRevision.Value; value += step) {
            token.ThrowIfCancellationRequested();
            var snapshot = snapshots[(int)value];
            if (query.Paths.Count > 0 && !snapshot.Changes.Any(c => query.Paths.Any(p => p.IsRoot || p == c.Path || c.Path.Value.StartsWith(p.Value + "/", StringComparison.Ordinal)))) { continue; }
            yield return new(snapshot.Revision, snapshot.Properties, snapshot.Changes);
            if (query.Limit > 0 && ++count >= query.Limit) { yield break; }
            await Task.Yield();
        }
    }

    public async ValueTask<SvnRevision> CommitAsync(SvnCommitRequest request, CancellationToken token = default) {
        if (request.Changes.Select(c => c.Path).Distinct().Count() != request.Changes.Count) { throw new ArgumentException("Duplicate commit paths.", nameof(request)); }
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try {
            var snapshots = Snapshots;
            if (request.BaseRevision != snapshots[^1].Revision) { throw new SvnOutOfDateException(request.BaseRevision, snapshots[^1].Revision); }
            ValidateLocks(request);
            var revision = new SvnRevision(snapshots.Length);
            var nodes = snapshots[^1].Nodes.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal);
            var changed = new List<SvnChangedPath>();
            foreach (var change in request.Changes) { await ApplyAsync(nodes, snapshots, change, revision, changed, token).ConfigureAwait(false); }
            var next = new Snapshot[snapshots.Length + 1]; Array.Copy(snapshots, next, snapshots.Length);
            next[^1] = new(revision, Clone(request.RevisionProperties), nodes, changed); Volatile.Write(ref _snapshots, next);
            if (!request.KeepLocks) lock (_lockGate) foreach (var pair in request.LockTokens) if (_locks.GetValueOrDefault(pair.Key)?.Token == pair.Value) _locks.Remove(pair.Key);
            return revision;
        } finally { _gate.Release(); }
    }

    public async ValueTask ChangeRevisionPropertyAsync(SvnRevisionPropertyChange change, CancellationToken token = default) {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try {
            var snapshots = Snapshots; Check(change.Revision, snapshots); var index = (int)change.Revision.Value; var snapshot = snapshots[index];
            var old = RevProp(snapshot.Properties, change.Name);
            if (!change.IgnoreExpectedValue && !Equal(old, change.ExpectedValue)) { throw new SvnRevisionPropertyConflictException(change.Revision, change.Name); }
            var next = snapshots.ToArray(); next[index] = snapshot with { Properties = SetRevProp(snapshot.Properties, change.Name, change.Value) }; Volatile.Write(ref _snapshots, next);
        } finally { _gate.Release(); }
    }

    public ValueTask<SvnLock> LockAsync(SvnLockRequest request, CancellationToken token = default) {
        token.ThrowIfCancellationRequested(); var node = Snapshots[^1].Nodes.GetValueOrDefault(request.Path.Value) ?? throw new SvnPathNotFoundException(request.Path);
        if (node.Kind != SvnNodeKind.File) throw new SvnLockException(request.Path, "only files can be locked.");
        if (request.CurrentRevision is { } r && node.Changed.Value > r.Value) throw new SvnOutOfDateException(r, node.Changed);
        lock (_lockGate) {
            PurgeExpiredLocks();
            if (_locks.ContainsKey(request.Path) && !request.StealLock) throw new SvnLockException(request.Path, "already locked.");
            var value = new SvnLock("opaquelocktoken:" + Guid.NewGuid(), request.Path, request.Owner, request.Comment, DateTimeOffset.UtcNow, request.Expires);
            _locks[request.Path] = value; return ValueTask.FromResult(value);
        }
    }
    public ValueTask<SvnLock> RefreshLockAsync(SvnRepositoryPath path, string token, DateTimeOffset? expires, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lockGate) {
            PurgeExpiredLocks();
            var current = _locks.GetValueOrDefault(path) ?? throw new SvnLockException(path, "not locked.");
            if (current.Token != token) throw new SvnLockException(path, "token mismatch.");
            var refreshed = current with { Expires = expires }; _locks[path] = refreshed; return ValueTask.FromResult(refreshed);
        }
    }
    public ValueTask UnlockAsync(SvnRepositoryPath path, string? token, bool breakLock, CancellationToken cancellationToken = default) {
        lock (_lockGate) { PurgeExpiredLocks(); var value = _locks.GetValueOrDefault(path) ?? throw new SvnLockException(path, "not locked."); if (!breakLock && value.Token != token) throw new SvnLockException(path, "token mismatch."); _locks.Remove(path); return ValueTask.CompletedTask; }
    }
    public ValueTask<SvnLock?> GetLockAsync(SvnRepositoryPath path, CancellationToken token = default) { token.ThrowIfCancellationRequested(); lock (_lockGate) { PurgeExpiredLocks(); return ValueTask.FromResult(_locks.GetValueOrDefault(path)); } }
    public async IAsyncEnumerable<SvnLock> GetLocksAsync(SvnRepositoryPath path, [EnumeratorCancellation] CancellationToken token = default) {
        SvnLock[] values; lock (_lockGate) { PurgeExpiredLocks(); values = _locks.Values.Where(x => path.IsRoot || x.Path == path || x.Path.Value.StartsWith(path.Value + "/", StringComparison.Ordinal)).ToArray(); }
        foreach (var value in values) { token.ThrowIfCancellationRequested(); yield return value; await Task.Yield(); }
    }

    private async ValueTask ApplyAsync(Dictionary<string, Node> nodes, Snapshot[] snapshots, SvnCommitChange change, SvnRevision revision, List<SvnChangedPath> log, CancellationToken token) {
        if (change.Action == SvnCommitChangeAction.Delete) {
            var deleted = nodes.GetValueOrDefault(change.Path.Value) ?? throw new SvnPathNotFoundException(change.Path);
            foreach (var key in nodes.Keys.Where(x => x == change.Path.Value || x.StartsWith(change.Path.Value + "/", StringComparison.Ordinal)).ToArray()) nodes.Remove(key);
            log.Add(new(change.Path, SvnChangeAction.Delete, deleted.Kind, deleted.Kind == SvnNodeKind.File, false)); Touch(nodes, change.Path, revision); return;
        }
        if (change.Action == SvnCommitChangeAction.Copy) {
            var source = change.CopyFrom!; var sourceSnapshot = Get(source.Revision, snapshots);
            var root = sourceSnapshot.Nodes.GetValueOrDefault(source.Path.Value) ?? throw new SvnPathNotFoundException(source.Path);
            if (root.Kind != change.NodeKind) throw new SvnNodeKindMismatchException(source.Path, change.NodeKind);
            Parents(nodes, change.Path, revision);
            foreach (var pair in sourceSnapshot.Nodes.Where(x => x.Key == source.Path.Value || x.Key.StartsWith(source.Path.Value + "/", StringComparison.Ordinal))) {
                var suffix = pair.Key[source.Path.Value.Length..].TrimStart('/'); var target = suffix.Length == 0 ? change.Path.Value : change.Path.Value + "/" + suffix;
                var copy = pair.Value.Clone(); copy.Changed = revision; nodes.Add(target, copy);
            }
            Props(nodes[change.Path.Value], change.PropertyChanges); log.Add(new(change.Path, SvnChangeAction.Add, change.NodeKind, false, change.PropertyChanges.Count > 0, source.Path, source.Revision)); Touch(nodes, change.Path, revision); return;
        }
        var exists = nodes.TryGetValue(change.Path.Value, out var node);
        if (change.Action == SvnCommitChangeAction.Add && exists) throw new InvalidOperationException($"Path '{change.Path}' exists.");
        if (change.Action == SvnCommitChangeAction.Modify && !exists) throw new SvnPathNotFoundException(change.Path);
        if (exists && node!.Kind != change.NodeKind) throw new SvnNodeKindMismatchException(change.Path, change.NodeKind);
        Parents(nodes, change.Path, revision); var text = false;
        if (!exists) { var body = change.NodeKind == SvnNodeKind.File ? await Body(change, token) : null; node = new(change.NodeKind, body, [], revision); nodes.Add(change.Path.Value, node); text = body is not null; }
        else if (change.HasContent) { if (node!.Kind != SvnNodeKind.File) throw new SvnNodeKindMismatchException(change.Path, SvnNodeKind.File); node.Body = await Body(change, token); text = true; }
        Props(node!, change.PropertyChanges); node!.Changed = revision; log.Add(new(change.Path, exists ? SvnChangeAction.Modify : SvnChangeAction.Add, change.NodeKind, text, change.PropertyChanges.Count > 0)); Touch(nodes, change.Path, revision);
    }

    private void ValidateLocks(SvnCommitRequest request) {
        lock (_lockGate) {
            PurgeExpiredLocks();
            foreach (var change in request.Changes)
                foreach (var value in _locks.Values.Where(value => value.Path == change.Path ||
                    change.Action == SvnCommitChangeAction.Delete && change.NodeKind == SvnNodeKind.Directory && value.Path.Value.StartsWith(change.Path.Value + "/", StringComparison.Ordinal)))
                    if (request.LockTokens.GetValueOrDefault(value.Path) != value.Token) throw new SvnLockException(value.Path, "matching token required.");
        }
    }
    private void PurgeExpiredLocks() {
        foreach (var path in _locks.Where(pair => pair.Value.Expires is { } expires && expires <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray()) _locks.Remove(path);
    }
    private static async ValueTask<byte[]> Body(SvnCommitChange change, CancellationToken token) { await using var source = await change.OpenContentAsync(token); using var target = new MemoryStream(); await source.CopyToAsync(target, token); return target.ToArray(); }
    private static void Parents(Dictionary<string, Node> nodes, SvnRepositoryPath path, SvnRevision revision) { var parts = path.Value.Split('/'); for (var n = 1; n < parts.Length; n++) { var p = string.Join('/', parts.Take(n)); if (!nodes.ContainsKey(p)) nodes[p] = new(SvnNodeKind.Directory, null, [], revision); else if (nodes[p].Kind != SvnNodeKind.Directory) throw new SvnNodeKindMismatchException(new(p), SvnNodeKind.Directory); } }
    private static void Touch(Dictionary<string, Node> nodes, SvnRepositoryPath path, SvnRevision revision) { nodes[""].Changed = revision; var parts = path.Value.Split('/'); for (var n = 1; n < parts.Length; n++) nodes[string.Join('/', parts.Take(n))].Changed = revision; }
    private static void Props(Node node, IEnumerable<SvnPropertyChange> changes) { foreach (var c in changes) if (c.Value is { } v) node.Properties[c.Name] = v.ToArray(); else node.Properties.Remove(c.Name); }
    private Snapshot[] Snapshots => Volatile.Read(ref _snapshots);
    private Snapshot Get(SvnRevision revision) => Get(revision, Snapshots);
    private static Snapshot Get(SvnRevision revision, Snapshot[] snapshots) { Check(revision, snapshots); return snapshots[(int)revision.Value]; }
    private static void Check(SvnRevision revision, Snapshot[] snapshots) { if (revision.Value < 0 || revision.Value >= snapshots.Length) throw new SvnInvalidRevisionException(revision); }
    private static SvnRevisionProperties Clone(SvnRevisionProperties p) => new(p.Author, p.Date, p.LogMessage, new(p.CustomProperties.Select(x => new SvnProperty(x.Name, x.Value.Span))));
    private static ReadOnlyMemory<byte>? RevProp(SvnRevisionProperties p, string name) => name switch { "svn:author" => p.Author is null ? null : Encoding.UTF8.GetBytes(p.Author), "svn:date" => p.Date is null ? null : Encoding.UTF8.GetBytes(p.Date.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'")), "svn:log" => p.LogMessage is null ? null : Encoding.UTF8.GetBytes(p.LogMessage), _ => p.CustomProperties.FirstOrDefault(x => x.Name == name)?.Value };
    private static SvnRevisionProperties SetRevProp(SvnRevisionProperties p, string name, ReadOnlyMemory<byte>? value) {
        if (name == "svn:author") return p with { Author = value is null ? null : Encoding.UTF8.GetString(value.Value.Span) }; if (name == "svn:log") return p with { LogMessage = value is null ? null : Encoding.UTF8.GetString(value.Value.Span) };
        if (name == "svn:date") return p with { Date = value is null ? null : DateTimeOffset.TryParse(Encoding.UTF8.GetString(value.Value.Span), out var date) ? date : throw new ArgumentException("svn:date is invalid.") };
        var custom = p.CustomProperties.ToDictionary(x => x.Name, x => x.Value.ToArray()); if (value is null) custom.Remove(name); else custom[name] = value.Value.ToArray(); return p with { CustomProperties = new(custom.Select(x => new SvnProperty(x.Key, x.Value))) };
    }
    private static bool Equal(ReadOnlyMemory<byte>? a, ReadOnlyMemory<byte>? b) => a is null ? b is null : b is not null && a.Value.Span.SequenceEqual(b.Value.Span);

    private sealed record Snapshot(SvnRevision Revision, SvnRevisionProperties Properties, IReadOnlyDictionary<string, Node> Nodes, IReadOnlyList<SvnChangedPath> Changes);
    private sealed class Node(SvnNodeKind kind, byte[]? body, Dictionary<string, byte[]> properties, SvnRevision changed) {
        public SvnNodeKind Kind { get; } = kind; public byte[]? Body { get; set; } = body; public Dictionary<string, byte[]> Properties { get; } = properties; public SvnRevision Changed { get; set; } = changed;
        public Node Clone() => new(Kind, Body, Properties.ToDictionary(x => x.Key, x => x.Value.ToArray()), Changed);
    }
    private sealed class Root(SvnMemoryRepository repository, Snapshot snapshot) : ISvnRevisionRoot {
        public SvnRevision Revision => snapshot.Revision;
        public ValueTask<SvnNodeInfo?> GetNodeInfoAsync(SvnRepositoryPath path, CancellationToken token = default) { token.ThrowIfCancellationRequested(); if (!snapshot.Nodes.TryGetValue(path.Value, out var n)) return ValueTask.FromResult<SvnNodeInfo?>(null); var p = repository.Get(n.Changed).Properties; return ValueTask.FromResult<SvnNodeInfo?>(new(n.Kind, n.Body?.LongLength ?? 0, n.Properties.Count > 0, n.Changed, p.Date, p.Author)); }
        public ValueTask<Stream> OpenFileAsync(SvnRepositoryPath path, CancellationToken token = default) { token.ThrowIfCancellationRequested(); return snapshot.Nodes.TryGetValue(path.Value, out var n) && n.Kind == SvnNodeKind.File ? ValueTask.FromResult<Stream>(new MemoryStream(n.Body!, false)) : ValueTask.FromException<Stream>(new SvnPathNotFoundException(path)); }
        public async IAsyncEnumerable<SvnDirectoryEntry> GetDirectoryAsync(SvnRepositoryPath path, [EnumeratorCancellation] CancellationToken token = default) {
            if (!snapshot.Nodes.TryGetValue(path.Value, out var dir)) throw new SvnPathNotFoundException(path); if (dir.Kind != SvnNodeKind.Directory) throw new SvnNodeKindMismatchException(path, SvnNodeKind.Directory); var prefix = path.IsRoot ? "" : path.Value + "/";
            foreach (var x in snapshot.Nodes.Where(x => x.Key != path.Value && x.Key.StartsWith(prefix) && !x.Key[prefix.Length..].Contains('/')).OrderBy(x => x.Key)) { var info = await GetNodeInfoAsync(new(x.Key), token) ?? throw new InvalidOperationException(); yield return new(x.Key[prefix.Length..], info); }
        }
        public ValueTask<SvnPropertyCollection> GetPropertiesAsync(SvnRepositoryPath path, CancellationToken token = default) { token.ThrowIfCancellationRequested(); return snapshot.Nodes.TryGetValue(path.Value, out var n) ? ValueTask.FromResult(new SvnPropertyCollection(n.Properties.Select(x => new SvnProperty(x.Key, x.Value)))) : ValueTask.FromException<SvnPropertyCollection>(new SvnPathNotFoundException(path)); }
    }
}

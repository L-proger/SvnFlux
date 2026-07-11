namespace SvnFlux.Core;

public enum SvnCommitChangeAction { Add, Modify, Delete, Copy }

public sealed record SvnCopySource(SvnRepositoryPath Path, SvnRevision Revision);
public sealed record SvnPropertyChange {
    private SvnPropertyChange(string name, ReadOnlyMemory<byte>? value) { Name = name; Value = value; }
    public string Name { get; }
    public ReadOnlyMemory<byte>? Value { get; }
    public static SvnPropertyChange Set(string name, ReadOnlySpan<byte> value) { _ = new SvnProperty(name, value); return new(name, value.ToArray()); }
    public static SvnPropertyChange Delete(string name) { _ = new SvnProperty(name, []); return new(name, null); }
}

public sealed class SvnCommitChange {
    private readonly byte[]? _content;
    private readonly Func<CancellationToken, ValueTask<Stream>>? _openContent;

    private SvnCommitChange(SvnRepositoryPath path, SvnCommitChangeAction action, SvnNodeKind nodeKind, byte[]? content, SvnCopySource? copyFrom = null, IReadOnlyList<SvnPropertyChange>? propertyChanges = null, Func<CancellationToken, ValueTask<Stream>>? openContent = null) {
        if (path.IsRoot && (action != SvnCommitChangeAction.Modify || nodeKind != SvnNodeKind.Directory)) { throw new ArgumentException("The repository root only supports property changes.", nameof(path)); }
        Path = path;
        Action = action;
        NodeKind = nodeKind;
        _content = content;
        CopyFrom = copyFrom;
        PropertyChanges = propertyChanges ?? [];
        _openContent = openContent;
    }

    public SvnRepositoryPath Path { get; }
    public SvnCommitChangeAction Action { get; }
    public SvnNodeKind NodeKind { get; }
    public ReadOnlyMemory<byte> Content => _content ?? ReadOnlyMemory<byte>.Empty;
    public bool HasContent => _content is not null || _openContent is not null;
    public SvnCopySource? CopyFrom { get; }
    public IReadOnlyList<SvnPropertyChange> PropertyChanges { get; }

    public static SvnCommitChange AddFile(SvnRepositoryPath path, ReadOnlySpan<byte> content) => new(path, SvnCommitChangeAction.Add, SvnNodeKind.File, content.ToArray());
    public static SvnCommitChange ModifyFile(SvnRepositoryPath path, ReadOnlySpan<byte> content) => new(path, SvnCommitChangeAction.Modify, SvnNodeKind.File, content.ToArray());
    public static SvnCommitChange AddFileStream(SvnRepositoryPath path, Func<CancellationToken, ValueTask<Stream>> openContent) => new(path, SvnCommitChangeAction.Add, SvnNodeKind.File, null, openContent: openContent ?? throw new ArgumentNullException(nameof(openContent)));
    public static SvnCommitChange ModifyFileStream(SvnRepositoryPath path, Func<CancellationToken, ValueTask<Stream>> openContent) => new(path, SvnCommitChangeAction.Modify, SvnNodeKind.File, null, openContent: openContent ?? throw new ArgumentNullException(nameof(openContent)));
    public static SvnCommitChange AddDirectory(SvnRepositoryPath path) => new(path, SvnCommitChangeAction.Add, SvnNodeKind.Directory, null);
    public static SvnCommitChange Delete(SvnRepositoryPath path, SvnNodeKind nodeKind) => new(path, SvnCommitChangeAction.Delete, nodeKind, null);
    public static SvnCommitChange Copy(SvnRepositoryPath path, SvnNodeKind nodeKind, SvnCopySource source) => new(path, SvnCommitChangeAction.Copy, nodeKind, null, source ?? throw new ArgumentNullException(nameof(source)));
    public static SvnCommitChange ModifyProperties(SvnRepositoryPath path, SvnNodeKind nodeKind, IEnumerable<SvnPropertyChange> changes) => new(path, SvnCommitChangeAction.Modify, nodeKind, null, propertyChanges: changes.ToArray());
    public SvnCommitChange WithPropertyChanges(IEnumerable<SvnPropertyChange> changes) => new(Path, Action, NodeKind, _content, CopyFrom, changes.ToArray(), _openContent);
    public ValueTask<Stream> OpenContentAsync(CancellationToken cancellationToken = default) => _openContent is not null
        ? _openContent(cancellationToken)
        : _content is not null ? ValueTask.FromResult<Stream>(new MemoryStream(_content, writable: false)) : ValueTask.FromException<Stream>(new InvalidOperationException("This change has no file content."));
}

public sealed record SvnCommitRequest(SvnRevision BaseRevision, SvnRevisionProperties RevisionProperties, IReadOnlyList<SvnCommitChange> Changes) {
    public IReadOnlyDictionary<SvnRepositoryPath, string> LockTokens { get; init; } = new Dictionary<SvnRepositoryPath, string>();
    public bool KeepLocks { get; init; }
    public SvnCommitRequest(SvnRevision baseRevision, SvnRevisionProperties revisionProperties, IEnumerable<SvnCommitChange> changes)
        : this(baseRevision, revisionProperties, changes?.ToArray() ?? throw new ArgumentNullException(nameof(changes))) { }
}

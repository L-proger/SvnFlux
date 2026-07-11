using SvnFlux.Core;

namespace SvnFlux.Repository.FileSystem;

public sealed class SvnFileSystemChange
{
    private readonly byte[]? _content;
    private readonly Func<CancellationToken, ValueTask<Stream>>? _openContent;

    private SvnFileSystemChange(SvnRepositoryPath path, byte[]? content, bool isDelete, bool isDirectory, SvnCopySource? copyFrom = null, IReadOnlyList<SvnPropertyChange>? propertyChanges = null, Func<CancellationToken, ValueTask<Stream>>? openContent = null) {
        Path = path;
        _content = content;
        IsDelete = isDelete;
        IsDirectory = isDirectory;
        CopyFrom = copyFrom;
        PropertyChanges = propertyChanges ?? [];
        _openContent = openContent;
    }

    public SvnRepositoryPath Path { get; }
    public bool IsDelete { get; }
    public bool IsDirectory { get; }
    public SvnCopySource? CopyFrom { get; }
    public IReadOnlyList<SvnPropertyChange> PropertyChanges { get; }
    public ReadOnlyMemory<byte> Content => _content ?? ReadOnlyMemory<byte>.Empty;
    public bool HasContent => _content is not null || _openContent is not null;

    public static SvnFileSystemChange Write(SvnRepositoryPath path, ReadOnlySpan<byte> content)
    {
        if (path.IsRoot)
        {
            throw new ArgumentException("The repository root cannot be written as a file.", nameof(path));
        }

        return new SvnFileSystemChange(path, content.ToArray(), false, false);
    }

    public static SvnFileSystemChange WriteStream(SvnRepositoryPath path, Func<CancellationToken, ValueTask<Stream>> openContent) {
        if (path.IsRoot) { throw new ArgumentException("The repository root cannot be written as a file.", nameof(path)); }
        return new SvnFileSystemChange(path, null, false, false, openContent: openContent ?? throw new ArgumentNullException(nameof(openContent)));
    }

    public static SvnFileSystemChange AddDirectory(SvnRepositoryPath path) {
        if (path.IsRoot) { throw new ArgumentException("The repository root cannot be added.", nameof(path)); }
        return new SvnFileSystemChange(path, null, false, true);
    }

    public static SvnFileSystemChange Copy(SvnRepositoryPath path, SvnNodeKind kind, SvnCopySource source) {
        if (path.IsRoot) { throw new ArgumentException("The repository root cannot be copied.", nameof(path)); }
        return new SvnFileSystemChange(path, null, false, kind == SvnNodeKind.Directory, copyFrom: source);
    }

    public static SvnFileSystemChange ModifyProperties(SvnRepositoryPath path, SvnNodeKind kind, IReadOnlyList<SvnPropertyChange> changes) => new(path, null, false, kind == SvnNodeKind.Directory, propertyChanges: changes);
    public SvnFileSystemChange WithPropertyChanges(IReadOnlyList<SvnPropertyChange> changes) => new(Path, _content, IsDelete, IsDirectory, CopyFrom, changes, _openContent);
    public ValueTask<Stream> OpenContentAsync(CancellationToken cancellationToken = default) => _openContent is not null
        ? _openContent(cancellationToken)
        : _content is not null ? ValueTask.FromResult<Stream>(new MemoryStream(_content, writable: false)) : ValueTask.FromException<Stream>(new InvalidOperationException("This change has no content."));

    public static SvnFileSystemChange Delete(SvnRepositoryPath path)
    {
        if (path.IsRoot)
        {
            throw new ArgumentException("The repository root cannot be deleted.", nameof(path));
        }

        return new SvnFileSystemChange(path, null, true, false);
    }
}

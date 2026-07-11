using System.Runtime.CompilerServices;
using SvnFlux.Core;

namespace SvnFlux.Repository.FileSystem;

internal sealed class SvnFileSystemRevisionRoot : ISvnRevisionRoot
{
    private readonly SvnFileSystemRepository _repository;
    private readonly IReadOnlyDictionary<string, NodeDocument> _nodes;
    private readonly string _treePath;

    public SvnFileSystemRevisionRoot(
        SvnFileSystemRepository repository,
        SvnRevision revision,
        ManifestDocument manifest)
    {
        _repository = repository;
        Revision = revision;
        _nodes = manifest.Nodes.ToDictionary(node => node.Path, StringComparer.Ordinal);
        _treePath = repository.GetTreePath(revision);
    }

    public SvnRevision Revision { get; }

    public async ValueTask<SvnNodeInfo?> GetNodeInfoAsync(
        SvnRepositoryPath path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_nodes.TryGetValue(path.Value, out var node))
        {
            return null;
        }

        var physicalPath = SvnFileSystemRepository.GetPhysicalPath(_treePath, path);
        if (node.Kind == "directory")
        {
            if (!Directory.Exists(physicalPath))
            {
                return null;
            }

            var properties = await _repository.GetNodeRevisionPropertiesAsync(node.LastChangedRevision, cancellationToken).ConfigureAwait(false);
            return new SvnNodeInfo(
                SvnNodeKind.Directory,
                0,
                node.Properties.Count != 0,
                new SvnRevision(node.LastChangedRevision),
                properties.Date,
                properties.Author);
        }

        if (!File.Exists(physicalPath))
        {
            return null;
        }

        await using var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
        var revisionProperties = await _repository.GetNodeRevisionPropertiesAsync(node.LastChangedRevision, cancellationToken).ConfigureAwait(false);
        return new SvnNodeInfo(
            SvnNodeKind.File,
            stream.Length,
            node.Properties.Count != 0,
            new SvnRevision(node.LastChangedRevision),
            revisionProperties.Date,
            revisionProperties.Author);
    }

    public ValueTask<Stream> OpenFileAsync(
        SvnRepositoryPath path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_nodes.TryGetValue(path.Value, out var node) || node.Kind != "file")
        {
            return ValueTask.FromException<Stream>(new SvnPathNotFoundException(path));
        }

        var physicalPath = SvnFileSystemRepository.GetPhysicalPath(_treePath, path);
        if (!File.Exists(physicalPath))
        {
            return ValueTask.FromException<Stream>(new SvnPathNotFoundException(path));
        }

        return ValueTask.FromResult<Stream>(new FileStream(
            physicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public async IAsyncEnumerable<SvnDirectoryEntry> GetDirectoryAsync(
        SvnRepositoryPath path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_nodes.TryGetValue(path.Value, out var directory) || directory.Kind != "directory")
        {
            throw new SvnPathNotFoundException(path);
        }

        var prefix = path.IsRoot ? string.Empty : path.Value + "/";
        var children = _nodes.Values
            .Where(node => node.Path != path.Value)
            .Where(node => node.Path.StartsWith(prefix, StringComparison.Ordinal))
            .Where(node => !node.Path[prefix.Length..].Contains('/'))
            .OrderBy(node => node.Path, StringComparer.Ordinal)
            .ToArray();
        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childPath = new SvnRepositoryPath(child.Path);
            var info = await GetNodeInfoAsync(childPath, cancellationToken).ConfigureAwait(false);
            if (info is not null)
            {
                yield return new SvnDirectoryEntry(child.Path[prefix.Length..], info);
            }
        }
    }

    public ValueTask<SvnPropertyCollection> GetPropertiesAsync(
        SvnRepositoryPath path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _nodes.TryGetValue(path.Value, out var node)
            ? ValueTask.FromResult(StorageModels.DeserializeProperties(node.Properties))
            : ValueTask.FromException<SvnPropertyCollection>(new SvnPathNotFoundException(path));
    }
}

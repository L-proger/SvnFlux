using System.Security.Cryptography;
using System.Globalization;
using SvnFlux.Core;
using SvnFlux.RaSvn.Protocol;
using SvnFlux.RaSvn.Wire;
using SvnFlux.Svndiff;
using static SvnFlux.RaSvn.Protocol.SvnProtocolItems;

namespace SvnFlux.RaSvn.Server;

internal sealed class SvnUpdateEditorDriver
{
    private const int FileChunkSize = 64 * 1024;
    private readonly SvnWireWriter _writer;
    private readonly ISvnRepository _repository;
    private readonly ISvnRevisionRoot _baseRoot;
    private readonly ISvnRevisionRoot _targetRoot;
    private readonly SvnRepositoryPath _baseAnchorPath;
    private readonly SvnRepositoryPath _targetAnchorPath;
    private readonly Guid _repositoryId;
    private readonly SvnDiffVersion _svndiffVersion;
    private readonly Action<string>? _diagnosticLog;
    private readonly Func<CancellationToken, ValueTask> _checkForEarlyError;
    private int _nextToken;

    public SvnUpdateEditorDriver(
        SvnWireWriter writer,
        ISvnRepository repository,
        ISvnRevisionRoot baseRoot,
        ISvnRevisionRoot targetRoot,
        SvnRepositoryPath baseAnchorPath,
        SvnRepositoryPath targetAnchorPath,
        Guid repositoryId,
        SvnDiffVersion svndiffVersion,
        Action<string>? diagnosticLog,
        Func<CancellationToken, ValueTask> checkForEarlyError)
    {
        _writer = writer;
        _repository = repository;
        _baseRoot = baseRoot;
        _targetRoot = targetRoot;
        _baseAnchorPath = baseAnchorPath;
        _targetAnchorPath = targetAnchorPath;
        _repositoryId = repositoryId;
        _svndiffVersion = svndiffVersion;
        _diagnosticLog = diagnosticLog;
        _checkForEarlyError = checkForEarlyError;
    }

    public async ValueTask DriveAsync(bool startEmpty, IReadOnlyList<SetPathReportCommand> pathReports, IReadOnlyList<SvnRepositoryPath> deletedPaths, CancellationToken cancellationToken, SvnRepositoryPath? scope = null) {
        var targetTree = await ReadTreeAsync(_targetRoot, _targetAnchorPath, cancellationToken).ConfigureAwait(false);
        var baseTree = startEmpty
            ? await ReadEmptyBaseTreeAsync(cancellationToken).ConfigureAwait(false)
            : await ReadTreeAsync(_baseRoot, _baseAnchorPath, cancellationToken).ConfigureAwait(false);
        if (scope is { IsRoot: false } value) {
            RestrictToScope(baseTree, value.Value);
            RestrictToScope(targetTree, value.Value);
        }
        await ApplyReportOverridesAsync(baseTree, targetTree, pathReports, deletedPaths, cancellationToken).ConfigureAwait(false);

        await WriteCommandAsync("target-rev", [Number(_targetRoot.Revision.Value)], cancellationToken).ConfigureAwait(false);
        const string rootToken = "d0";
        await WriteCommandAsync(
            "open-root",
            [List(Number(_baseRoot.Revision.Value)), Text(rootToken)],
            cancellationToken).ConfigureAwait(false);
        await WritePropertyChangesAsync(rootToken, baseTree[string.Empty].Properties, targetTree[string.Empty].Properties, isFile: false, cancellationToken).ConfigureAwait(false);
        await WriteEntryPropertiesAsync(rootToken, targetTree[string.Empty].Info, isFile: false, cancellationToken).ConfigureAwait(false);
        await DriveDirectoryAsync(string.Empty, rootToken, baseTree, targetTree, cancellationToken).ConfigureAwait(false);
        await WriteCommandAsync("close-dir", [Text(rootToken)], cancellationToken).ConfigureAwait(false);
        await WriteCommandAsync("close-edit", [], cancellationToken).ConfigureAwait(false);
    }

    private static void RestrictToScope(Dictionary<string, TreeNode> tree, string scope) {
        foreach (var key in tree.Keys.Where(key => key.Length != 0 && key != scope && !key.StartsWith(scope + "/", StringComparison.Ordinal) && !scope.StartsWith(key + "/", StringComparison.Ordinal)).ToArray()) { tree.Remove(key); }
    }

    private async ValueTask ApplyReportOverridesAsync(Dictionary<string, TreeNode> baseTree, IReadOnlyDictionary<string, TreeNode> targetTree, IReadOnlyList<SetPathReportCommand> pathReports, IReadOnlyList<SvnRepositoryPath> deletedPaths, CancellationToken cancellationToken) {
        foreach (var report in pathReports) {
            if (report.StartEmpty) {
                RemoveSubtree(baseTree, report.Path.Value);
                continue;
            }
            var reportedRoot = await _repository.OpenRevisionAsync(report.Revision, cancellationToken).ConfigureAwait(false);
            var reportedTree = await ReadTreeAsync(reportedRoot, _baseAnchorPath, cancellationToken).ConfigureAwait(false);
            ReplaceSubtree(baseTree, reportedTree, report.Path.Value, report.Revision == _targetRoot.Revision);
        }

        foreach (var path in deletedPaths) { ReplaceSubtree(baseTree, targetTree, path.Value, true); }
    }

    private static void ReplaceSubtree(Dictionary<string, TreeNode> destination, IReadOnlyDictionary<string, TreeNode> source, string path, bool isReportedTarget) {
        RemoveSubtree(destination, path);
        foreach (var pair in source.Where(pair => pair.Key == path || pair.Key.StartsWith(path + "/", StringComparison.Ordinal))) { destination[pair.Key] = pair.Value with { IsReportedTarget = isReportedTarget }; }
    }

    private static void RemoveSubtree(Dictionary<string, TreeNode> tree, string path) {
        foreach (var key in tree.Keys.Where(key => key == path || key.StartsWith(path + "/", StringComparison.Ordinal)).ToArray()) { tree.Remove(key); }
    }

    private async ValueTask DriveDirectoryAsync(
        string directoryPath,
        string directoryToken,
        IReadOnlyDictionary<string, TreeNode> baseTree,
        IReadOnlyDictionary<string, TreeNode> targetTree,
        CancellationToken cancellationToken)
    {
        var childNames = GetImmediateChildren(baseTree, directoryPath)
            .Concat(GetImmediateChildren(targetTree, directoryPath))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (var childName in childNames)
        {
            var childPath = directoryPath.Length == 0 ? childName : directoryPath + "/" + childName;
            baseTree.TryGetValue(childPath, out var baseNode);
            targetTree.TryGetValue(childPath, out var targetNode);
            if (baseNode is not null && (targetNode is null || baseNode.Info.Kind != targetNode.Info.Kind))
            {
                await WriteCommandAsync(
                    "delete-entry",
                    [Text(childPath), List(Number(baseNode.Info.LastChangedRevision.Value)), Text(directoryToken)],
                    cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var childName in childNames)
        {
            var childPath = directoryPath.Length == 0 ? childName : directoryPath + "/" + childName;
            baseTree.TryGetValue(childPath, out var baseNode);
            targetTree.TryGetValue(childPath, out var targetNode);

            if (targetNode is null)
            {
                continue;
            }

            if (baseNode is not null && baseNode.Info.Kind != targetNode.Info.Kind)
            {
                baseNode = null;
            }

            if (targetNode.Info.Kind == SvnNodeKind.Directory)
            {
                var childToken = NextToken("d");
                if (baseNode is null)
                {
                    await WriteCommandAsync(
                        "add-dir",
                        [Text(childPath), Text(directoryToken), Text(childToken), EmptyList()],
                        cancellationToken).ConfigureAwait(false);
                    await WritePropertyChangesAsync(childToken, SvnPropertyCollection.Empty, targetNode.Properties, isFile: false, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await WriteCommandAsync(
                        "open-dir",
                        [Text(childPath), Text(directoryToken), Text(childToken), List(Number(baseNode.Root.Revision.Value))],
                        cancellationToken).ConfigureAwait(false);
                    await WritePropertyChangesAsync(childToken, baseNode.Properties, targetNode.Properties, isFile: false, cancellationToken).ConfigureAwait(false);
                }

                await WriteEntryPropertiesAsync(childToken, targetNode.Info, isFile: false, cancellationToken).ConfigureAwait(false);

                await DriveDirectoryAsync(childPath, childToken, baseTree, targetTree, cancellationToken).ConfigureAwait(false);
                await WriteCommandAsync("close-dir", [Text(childToken)], cancellationToken).ConfigureAwait(false);
                continue;
            }

            await DriveFileAsync(childPath, directoryToken, baseNode, targetNode, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask DriveFileAsync(
        string path,
        string parentToken,
        TreeNode? baseNode,
        TreeNode targetNode,
        CancellationToken cancellationToken)
    {
        if (baseNode?.IsReportedTarget == true) { return; }
        var contentChanged = baseNode is null || !await FileContentsEqualAsync(path, baseNode, targetNode, cancellationToken).ConfigureAwait(false);
        if (baseNode is not null &&
            !contentChanged &&
            PropertiesEqual(baseNode.Properties, targetNode.Properties) &&
            EntryMetadataEqual(baseNode.Info, targetNode.Info))
        {
            return;
        }

        var token = NextToken("f");
        if (baseNode is null)
        {
            await WriteCommandAsync(
                "add-file",
                [Text(path), Text(parentToken), Text(token), EmptyList()],
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WriteCommandAsync(
                "open-file",
                [Text(path), Text(parentToken), Text(token), List(Number(baseNode.Root.Revision.Value))],
                cancellationToken).ConfigureAwait(false);
        }

        await WritePropertyChangesAsync(
            token,
            baseNode?.Properties ?? SvnPropertyCollection.Empty,
            targetNode.Properties,
            isFile: true,
            cancellationToken).ConfigureAwait(false);
        await WriteEntryPropertiesAsync(token, targetNode.Info, isFile: true, cancellationToken).ConfigureAwait(false);

        string? checksum = null;
        if (contentChanged)
        {
            checksum = await WriteTextDeltaAsync(path, token, baseNode, targetNode, cancellationToken).ConfigureAwait(false);
        }

        await WriteCommandAsync(
            "close-file",
            [Text(token), checksum is null ? EmptyList() : List(Text(checksum))],
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<string> WriteTextDeltaAsync(
        string relativePath,
        string token,
        TreeNode? baseNode,
        TreeNode targetNode,
        CancellationToken cancellationToken)
    {
        var baseChecksum = baseNode is not null
            ? List(Text(await ComputeFileChecksumAsync(baseNode.Root, baseNode.Path, cancellationToken).ConfigureAwait(false)))
            : EmptyList();
        await WriteCommandAsync("apply-textdelta", [Text(token), baseChecksum], cancellationToken).ConfigureAwait(false);
        await using var targetStream = await targetNode.Root.OpenFileAsync(targetNode.Path, cancellationToken).ConfigureAwait(false);
        await using var baseStream = baseNode is not null
            ? await baseNode.Root.OpenFileAsync(baseNode.Path, cancellationToken).ConfigureAwait(false)
            : null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var targetBuffer = new byte[FileChunkSize];
        var sourceBuffer = new byte[FileChunkSize];
        long sourceViewOffset = 0;
        var first = true;
        while (true)
        {
            var targetCount = await ReadChunkAsync(targetStream, targetBuffer, cancellationToken).ConfigureAwait(false);
            if (targetCount == 0)
            {
                break;
            }

            var sourceCount = baseStream is null
                ? 0
                : await ReadChunkAsync(baseStream, sourceBuffer, cancellationToken).ConfigureAwait(false);
            hash.AppendData(targetBuffer, 0, targetCount);
            var window = SvnDiffEncoder.EncodeWindow(
                sourceViewOffset,
                sourceBuffer.AsSpan(0, sourceCount),
                targetBuffer.AsSpan(0, targetCount),
                _svndiffVersion);
            byte[] encoded;
            if (first)
            {
                var header = SvnDiffEncoder.EncodeHeader(_svndiffVersion);
                encoded = new byte[header.Length + window.Length];
                header.CopyTo(encoded, 0);
                window.CopyTo(encoded, header.Length);
            }
            else
            {
                encoded = window;
            }

            first = false;
            await WriteCommandAsync("textdelta-chunk", [Text(token), new SvnWireString(encoded)], cancellationToken).ConfigureAwait(false);
            sourceViewOffset += sourceCount;
        }

        if (first)
        {
            await WriteCommandAsync(
                "textdelta-chunk",
                [Text(token), new SvnWireString(SvnDiffEncoder.EncodeHeader(_svndiffVersion))],
                cancellationToken).ConfigureAwait(false);
        }

        await WriteCommandAsync("textdelta-end", [Text(token)], cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async ValueTask<int> ReadChunkAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[count..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        return count;
    }

    private static async ValueTask<string> ComputeFileChecksumAsync(
        ISvnRevisionRoot root,
        SvnRepositoryPath path,
        CancellationToken cancellationToken)
    {
        await using var stream = await root.OpenFileAsync(path, cancellationToken).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[FileChunkSize];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            hash.AppendData(buffer, 0, count);
        }
    }

    private async ValueTask<bool> FileContentsEqualAsync(string relativePath, TreeNode baseNode, TreeNode targetNode, CancellationToken cancellationToken)
    {
        await using var baseStream = await baseNode.Root.OpenFileAsync(baseNode.Path, cancellationToken).ConfigureAwait(false);
        await using var targetStream = await targetNode.Root.OpenFileAsync(targetNode.Path, cancellationToken).ConfigureAwait(false);
        if (baseStream.CanSeek && targetStream.CanSeek && baseStream.Length != targetStream.Length)
        {
            return false;
        }

        var baseBuffer = new byte[FileChunkSize];
        var targetBuffer = new byte[FileChunkSize];
        while (true)
        {
            var baseCount = await baseStream.ReadAsync(baseBuffer, cancellationToken).ConfigureAwait(false);
            var targetCount = await targetStream.ReadAsync(targetBuffer, cancellationToken).ConfigureAwait(false);
            if (baseCount != targetCount || !baseBuffer.AsSpan(0, baseCount).SequenceEqual(targetBuffer.AsSpan(0, targetCount)))
            {
                return false;
            }

            if (baseCount == 0)
            {
                return true;
            }
        }
    }

    private async ValueTask<Dictionary<string, TreeNode>> ReadTreeAsync(
        ISvnRevisionRoot root,
        SvnRepositoryPath anchorPath,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
        var rootInfo = await root.GetNodeInfoAsync(anchorPath, cancellationToken).ConfigureAwait(false)
            ?? throw new SvnPathNotFoundException(anchorPath);
        if (rootInfo.Kind != SvnNodeKind.Directory)
        {
            throw new SvnNodeKindMismatchException(anchorPath, SvnNodeKind.Directory);
        }

        await ReadNodeAsync(root, string.Empty, anchorPath, rootInfo, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<Dictionary<string, TreeNode>> ReadEmptyBaseTreeAsync(CancellationToken cancellationToken)
    {
        var rootInfo = await _baseRoot.GetNodeInfoAsync(_baseAnchorPath, cancellationToken).ConfigureAwait(false)
            ?? await _targetRoot.GetNodeInfoAsync(_targetAnchorPath, cancellationToken).ConfigureAwait(false)
            ?? throw new SvnPathNotFoundException(_baseAnchorPath);
        return new Dictionary<string, TreeNode>(StringComparer.Ordinal)
        {
            [string.Empty] = new(_baseRoot, _baseAnchorPath, rootInfo, SvnPropertyCollection.Empty)
        };
    }

    private static async ValueTask ReadNodeAsync(
        ISvnRevisionRoot root,
        string relativePath,
        SvnRepositoryPath absolutePath,
        SvnNodeInfo info,
        Dictionary<string, TreeNode> result,
        CancellationToken cancellationToken)
    {
        var properties = await root.GetPropertiesAsync(absolutePath, cancellationToken).ConfigureAwait(false);
        result.Add(relativePath, new TreeNode(root, absolutePath, info, properties));
        if (info.Kind != SvnNodeKind.Directory)
        {
            return;
        }

        await foreach (var entry in root.GetDirectoryAsync(absolutePath, cancellationToken).ConfigureAwait(false))
        {
            var childRelativePath = relativePath.Length == 0 ? entry.Name : relativePath + "/" + entry.Name;
            await ReadNodeAsync(
                root,
                childRelativePath,
                absolutePath.Append(new SvnRepositoryPath(entry.Name)),
                entry.NodeInfo,
                result,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask WritePropertyChangesAsync(
        string token,
        SvnPropertyCollection baseProperties,
        SvnPropertyCollection targetProperties,
        bool isFile,
        CancellationToken cancellationToken)
    {
        var baseMap = baseProperties.ToDictionary(property => property.Name, StringComparer.Ordinal);
        var targetMap = targetProperties.ToDictionary(property => property.Name, StringComparer.Ordinal);
        foreach (var name in baseMap.Keys.Concat(targetMap.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            baseMap.TryGetValue(name, out var baseProperty);
            targetMap.TryGetValue(name, out var targetProperty);
            if (baseProperty is not null && targetProperty is not null && baseProperty.Value.Span.SequenceEqual(targetProperty.Value.Span))
            {
                continue;
            }

            await WriteCommandAsync(
                isFile ? "change-file-prop" : "change-dir-prop",
                [Text(token), Text(name), targetProperty is null ? EmptyList() : List(new SvnWireString(targetProperty.Value))],
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask WriteEntryPropertiesAsync(
        string token,
        SvnNodeInfo info,
        bool isFile,
        CancellationToken cancellationToken)
    {
        var command = isFile ? "change-file-prop" : "change-dir-prop";
        await WriteCommandAsync(
            command,
            [Text(token), Text("svn:entry:committed-rev"), List(Text(info.LastChangedRevision.Value.ToString(CultureInfo.InvariantCulture)))],
            cancellationToken).ConfigureAwait(false);
        await WriteCommandAsync(
            command,
            [Text(token), Text("svn:entry:uuid"), List(Text(_repositoryId.ToString()))],
            cancellationToken).ConfigureAwait(false);
        if (info.LastChangedTime is { } changedTime)
        {
            await WriteCommandAsync(
                command,
                [Text(token), Text("svn:entry:committed-date"), List(Text(changedTime.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)))],
                cancellationToken).ConfigureAwait(false);
        }

        if (info.LastChangedAuthor is { } author)
        {
            await WriteCommandAsync(
                command,
                [Text(token), Text("svn:entry:last-author"), List(Text(author))],
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask WriteCommandAsync(
        string name,
        IReadOnlyList<SvnWireItem> parameters,
        CancellationToken cancellationToken)
    {
        _diagnosticLog?.Invoke($"Sending update editor command {name}.");
        await _writer.WriteItemAsync(List(Word(name), new SvnWireList(parameters)), cancellationToken).ConfigureAwait(false);
        if (name != "close-edit")
        {
            await _checkForEarlyError(cancellationToken).ConfigureAwait(false);
        }
    }

    private string NextToken(string prefix) => prefix + Interlocked.Increment(ref _nextToken);

    private static IEnumerable<string> GetImmediateChildren(IReadOnlyDictionary<string, TreeNode> tree, string directoryPath)
    {
        var prefix = directoryPath.Length == 0 ? string.Empty : directoryPath + "/";
        return tree.Keys
            .Where(path => path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(path => path[prefix.Length..])
            .Where(path => !path.Contains('/'));
    }

    private static bool PropertiesEqual(SvnPropertyCollection left, SvnPropertyCollection right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var rightMap = right.ToDictionary(property => property.Name, StringComparer.Ordinal);
        return left.All(property =>
            rightMap.TryGetValue(property.Name, out var other) &&
            property.Value.Span.SequenceEqual(other.Value.Span));
    }

    private static bool EntryMetadataEqual(SvnNodeInfo left, SvnNodeInfo right) =>
        left.LastChangedRevision == right.LastChangedRevision &&
        left.LastChangedTime == right.LastChangedTime &&
        string.Equals(left.LastChangedAuthor, right.LastChangedAuthor, StringComparison.Ordinal);

    private sealed record TreeNode(ISvnRevisionRoot Root, SvnRepositoryPath Path, SvnNodeInfo Info, SvnPropertyCollection Properties, bool IsReportedTarget = false);
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Http;
using SvnFlux.Core;
using SvnFlux.Svndiff;

namespace SvnFlux.Http;

internal static class SvnHttpReplayReport {
    private const string ProtocolNamespace = "svn:";
    private const int FileChunkSize = 64 * 1024;

    internal static async Task WriteAsync(HttpRequest request, HttpResponse response, ISvnRepository repository,
        SvnHttpResource resource, SvnDiffVersion version, long maximumSize, CancellationToken token) {
        var replay = await ReadAsync(request, resource, maximumSize, token).ConfigureAwait(false);
        var targetRoot = await repository.OpenRevisionAsync(replay.Revision, token).ConfigureAwait(false);
        var baseRevision = new SvnRevision(Math.Max(0, replay.Revision.Value - 1));
        var baseRoot = await repository.OpenRevisionAsync(baseRevision, token).ConfigureAwait(false);
        var tree = await BuildTreeAsync(repository, replay.Path, replay.Revision, token).ConfigureAwait(false);
        var oldRootProperties = await baseRoot.GetNodeInfoAsync(replay.Path, token).ConfigureAwait(false) is null
            ? SvnPropertyCollection.Empty : await baseRoot.GetPropertiesAsync(replay.Path, token).ConfigureAwait(false);

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/xml; charset=utf-8";
        await using var writer = XmlWriter.Create(response.Body, new() { Async = true, Encoding = new UTF8Encoding(false), CloseOutput = false });
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        writer.WriteStartElement("S", "editor-report", ProtocolNamespace);
        Empty(writer, "target-revision", ("rev", replay.Revision.Value.ToString(CultureInfo.InvariantCulture)));
        Empty(writer, "open-root", ("rev", baseRevision.Value.ToString(CultureInfo.InvariantCulture)));
        WriteProperties(writer, "dir", oldRootProperties,
            await targetRoot.GetPropertiesAsync(replay.Path, token).ConfigureAwait(false), token);
        await WriteChildrenAsync(writer, repository, tree, targetRoot, baseRoot, replay.Path, baseRevision, replay.LowWaterMark, version, token).ConfigureAwait(false);
        Empty(writer, "close-directory");
        writer.WriteEndElement();
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private static async ValueTask<ReplayNode> BuildTreeAsync(ISvnRepository repository, SvnRepositoryPath basePath, SvnRevision revision, CancellationToken token) {
        var root = new ReplayNode(basePath, "");
        await foreach (var entry in repository.GetLogAsync(new([], revision, revision), token).ConfigureAwait(false)) {
            foreach (var change in entry.ChangedPaths) {
                if (!Contains(basePath, change.Path)) continue;
                var relative = Relative(basePath, change.Path);
                var node = root;
                if (relative.Length != 0) {
                    var target = basePath;
                    var current = "";
                    foreach (var segment in relative.Split('/')) {
                        target = target.IsRoot ? new(segment) : target.Append(new(segment));
                        current = current.Length == 0 ? segment : current + "/" + segment;
                        if (!node.Children.TryGetValue(segment, out var child))
                            node.Children[segment] = child = new(target, current);
                        node = child;
                    }
                }
                node.Change = change;
            }
        }
        return root;
    }

    private static async ValueTask WriteChildrenAsync(XmlWriter writer, ISvnRepository repository, ReplayNode node, ISvnRevisionRoot targetRoot,
        ISvnRevisionRoot? baseRoot, SvnRepositoryPath basePath, SvnRevision baseRevision, SvnRevision lowWaterMark,
        SvnDiffVersion version, CancellationToken token) {
        foreach (var child in node.Children.Values.OrderBy(value => value.RelativePath, StringComparer.Ordinal))
            await WriteNodeAsync(writer, repository, child, targetRoot, baseRoot, Child(basePath, child.Name), baseRevision, lowWaterMark, version, token).ConfigureAwait(false);
    }

    private static async ValueTask WriteNodeAsync(XmlWriter writer, ISvnRepository repository, ReplayNode node, ISvnRevisionRoot targetRoot,
        ISvnRevisionRoot? inheritedBaseRoot, SvnRepositoryPath inheritedBasePath, SvnRevision inheritedBaseRevision,
        SvnRevision lowWaterMark, SvnDiffVersion version, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        var change = node.Change;
        var targetInfo = await targetRoot.GetNodeInfoAsync(node.Path, token).ConfigureAwait(false);
        var baseRoot = inheritedBaseRoot;
        var basePath = inheritedBasePath;
        var baseRevision = inheritedBaseRevision;
        SvnCopySource? copy = null;
        var deleteRevision = inheritedBaseRevision;
        if (change?.CopyFromPath is { } copyPath && change.CopyFromRevision is { } copyRevision && copyRevision.Value >= lowWaterMark.Value) {
            copy = new(copyPath, copyRevision);
            baseRoot = await repository.OpenRevisionAsync(copyRevision, token).ConfigureAwait(false);
            basePath = copyPath;
            baseRevision = copyRevision;
        }
        var baseInfo = baseRoot is null ? null : await baseRoot.GetNodeInfoAsync(basePath, token).ConfigureAwait(false);

        if (change?.Action is SvnChangeAction.Delete or SvnChangeAction.Replace)
            Empty(writer, "delete-entry", ("name", node.RelativePath), ("rev", deleteRevision.Value.ToString(CultureInfo.InvariantCulture)));
        if (change?.Action == SvnChangeAction.Delete) return;
        if (targetInfo is null) throw new SvnPathNotFoundException(node.Path);

        var adding = change?.Action is SvnChangeAction.Add or SvnChangeAction.Replace || baseInfo is null;
        if (adding && copy is null) { baseRoot = null; baseInfo = null; }
        if (targetInfo.Kind == SvnNodeKind.Directory) {
            if (adding)
                Empty(writer, "add-directory", ("name", node.RelativePath),
                    copy is null ? null : ("copyfrom-path", "/" + copy.Path.Value),
                    copy is null ? null : ("copyfrom-rev", copy.Revision.Value.ToString(CultureInfo.InvariantCulture)));
            else
                Empty(writer, "open-directory", ("name", node.RelativePath), ("rev", baseRevision.Value.ToString(CultureInfo.InvariantCulture)));
            var oldProperties = baseRoot is null ? SvnPropertyCollection.Empty : await baseRoot.GetPropertiesAsync(basePath, token).ConfigureAwait(false);
            WriteProperties(writer, "dir", oldProperties, await targetRoot.GetPropertiesAsync(node.Path, token).ConfigureAwait(false), token);
            var children = node.Children.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (adding && baseRoot is null) {
                await foreach (var entry in targetRoot.GetDirectoryAsync(node.Path, token).ConfigureAwait(false)) {
                    if (children.ContainsKey(entry.Name)) continue;
                    var path = node.Path.Append(new(entry.Name));
                    var relative = node.RelativePath + "/" + entry.Name;
                    children[entry.Name] = new(path, relative);
                }
            }
            foreach (var child in children.Values.OrderBy(value => value.RelativePath, StringComparer.Ordinal))
                await WriteNodeAsync(writer, repository, child, targetRoot, baseRoot, Child(basePath, child.Name), baseRevision, lowWaterMark, version, token).ConfigureAwait(false);
            Empty(writer, "close-directory");
            return;
        }

        if (adding)
            Empty(writer, "add-file", ("name", node.RelativePath),
                copy is null ? null : ("copyfrom-path", "/" + copy.Path.Value),
                copy is null ? null : ("copyfrom-rev", copy.Revision.Value.ToString(CultureInfo.InvariantCulture)));
        else
            Empty(writer, "open-file", ("name", node.RelativePath), ("rev", baseRevision.Value.ToString(CultureInfo.InvariantCulture)));
        var baseProperties = baseRoot is null ? SvnPropertyCollection.Empty : await baseRoot.GetPropertiesAsync(basePath, token).ConfigureAwait(false);
        WriteProperties(writer, "file", baseProperties, await targetRoot.GetPropertiesAsync(node.Path, token).ConfigureAwait(false), token);
        var writeText = baseRoot is null || change?.TextModified != false;
        var checksum = writeText
            ? await WriteTextAsync(writer, targetRoot, node.Path, version, token).ConfigureAwait(false)
            : await ChecksumAsync(targetRoot, node.Path, token).ConfigureAwait(false);
        Empty(writer, "close-file", ("checksum", checksum));
    }

    private static void WriteProperties(XmlWriter writer, string kind, SvnPropertyCollection oldProperties,
        SvnPropertyCollection newProperties, CancellationToken token) {
        var old = oldProperties.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
        var current = newProperties.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
        foreach (var name in old.Keys.Except(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            Empty(writer, "change-" + kind + "-prop", ("name", name), ("del", "true"));
        foreach (var pair in current.OrderBy(value => value.Key, StringComparer.Ordinal)) {
            token.ThrowIfCancellationRequested();
            if (old.TryGetValue(pair.Key, out var previous) && previous.Span.SequenceEqual(pair.Value.Span)) continue;
            writer.WriteStartElement("S", "change-" + kind + "-prop", ProtocolNamespace);
            writer.WriteAttributeString("name", pair.Key);
            var bytes = pair.Value.ToArray();
            writer.WriteBase64(bytes, 0, bytes.Length);
            writer.WriteEndElement();
        }
    }

    private static async ValueTask<string> WriteTextAsync(XmlWriter writer, ISvnRevisionRoot root, SvnRepositoryPath path,
        SvnDiffVersion version, CancellationToken token) {
        writer.WriteStartElement("S", "apply-textdelta", ProtocolNamespace);
        var header = SvnDiffEncoder.EncodeHeader(version);
        await writer.WriteBase64Async(header, 0, header.Length).ConfigureAwait(false);
        await using var stream = await root.OpenFileAsync(path, token).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[FileChunkSize];
        while (true) {
            var count = await ReadChunkAsync(stream, buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            hash.AppendData(buffer, 0, count);
            var window = SvnDiffEncoder.EncodeWindow(0, ReadOnlySpan<byte>.Empty, buffer.AsSpan(0, count), version);
            await writer.WriteBase64Async(window, 0, window.Length).ConfigureAwait(false);
        }
        writer.WriteEndElement();
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async ValueTask<string> ChecksumAsync(ISvnRevisionRoot root, SvnRepositoryPath path, CancellationToken token) {
        await using var stream = await root.OpenFileAsync(path, token).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[FileChunkSize];
        while (true) {
            var count = await ReadChunkAsync(stream, buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async ValueTask<int> ReadChunkAsync(Stream stream, Memory<byte> buffer, CancellationToken token) {
        var count = 0;
        while (count < buffer.Length) {
            var read = await stream.ReadAsync(buffer[count..], token).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        return count;
    }

    private static void Empty(XmlWriter writer, string name, params (string Name, string Value)?[] attributes) {
        writer.WriteStartElement("S", name, ProtocolNamespace);
        foreach (var attribute in attributes) if (attribute is { } value) writer.WriteAttributeString(value.Name, value.Value);
        writer.WriteEndElement();
    }

    private static bool Contains(SvnRepositoryPath parent, SvnRepositoryPath path) =>
        parent.IsRoot || path == parent || path.Value.StartsWith(parent.Value + "/", StringComparison.Ordinal);

    private static string Relative(SvnRepositoryPath parent, SvnRepositoryPath path) =>
        parent.IsRoot ? path.Value : path == parent ? "" : path.Value[(parent.Value.Length + 1)..];

    private static SvnRepositoryPath Child(SvnRepositoryPath parent, string name) => parent.IsRoot ? new(name) : parent.Append(new(name));

    private static async ValueTask<Request> ReadAsync(HttpRequest request, SvnHttpResource resource, long maximumSize, CancellationToken token) {
        if (request.ContentLength > maximumSize) throw new BadHttpRequestException("The replay REPORT body is too large.", StatusCodes.Status413PayloadTooLarge);
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximumSize };
        using var reader = XmlReader.Create(request.Body, settings);
        if (await reader.MoveToContentAsync().ConfigureAwait(false) != XmlNodeType.Element || reader.LocalName != "replay-report" || reader.NamespaceURI != ProtocolNamespace)
            throw new BadHttpRequestException("Expected an svn:replay-report document.");
        SvnRevision? revision = resource.Revision;
        SvnRevision? lowWaterMark = null;
        SvnRepositoryPath? includePath = null;
        await reader.ReadAsync().ConfigureAwait(false);
        while (!reader.EOF) {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 0) break;
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1 || reader.NamespaceURI != ProtocolNamespace) {
                await reader.ReadAsync().ConfigureAwait(false);
                continue;
            }
            switch (reader.LocalName) {
                case "revision": revision = Revision(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false), "replay revision"); continue;
                case "low-water-mark": lowWaterMark = Revision(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false), "replay low-water mark"); continue;
                case "include-path": includePath = new(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false)); continue;
                case "send-deltas":
                    var value = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                    if (value is not ("0" or "1")) throw new BadHttpRequestException("The replay send-deltas value is invalid.");
                    continue;
            }
            await reader.ReadAsync().ConfigureAwait(false);
        }
        if (revision is null || lowWaterMark is null) throw new BadHttpRequestException("The replay revision or low-water mark is missing.");
        if (lowWaterMark.Value.Value > revision.Value.Value) throw new BadHttpRequestException("The replay low-water mark exceeds its revision.");
        var path = includePath ?? (resource.Kind is SvnHttpResourceKind.Public or SvnHttpResourceKind.RevisionRoot ? resource.Path : new(""));
        return new(revision.Value, lowWaterMark.Value, path);
    }

    private static SvnRevision Revision(string value, string field) {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0)
            throw new BadHttpRequestException($"The {field} is invalid.");
        return new(revision);
    }

    private sealed class ReplayNode(SvnRepositoryPath path, string relativePath) {
        public SvnRepositoryPath Path { get; } = path;
        public string RelativePath { get; } = relativePath;
        public string Name => RelativePath[(RelativePath.LastIndexOf('/') + 1)..];
        public SortedDictionary<string, ReplayNode> Children { get; } = new(StringComparer.Ordinal);
        public SvnChangedPath? Change { get; set; }
    }

    private sealed record Request(SvnRevision Revision, SvnRevision LowWaterMark, SvnRepositoryPath Path);

}

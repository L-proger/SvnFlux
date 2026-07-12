using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Http;
using SvnFlux.Core;
using SvnFlux.Svndiff;

namespace SvnFlux.Http;

internal sealed record SvnHttpUpdateEntry(SvnRepositoryPath Path, SvnRevision Revision, bool StartEmpty, string Depth, SvnRepositoryPath? LinkPath, string? LockToken = null);
internal sealed record SvnHttpUpdateRequest(string SourcePath, string? DestinationPath, SvnRevision? TargetRevision, SvnRepositoryPath UpdateTarget,
    string Depth, bool SendAll, bool SendCopyFromArguments, bool TextDeltas, IReadOnlyList<SvnHttpUpdateEntry> Entries, IReadOnlySet<SvnRepositoryPath> MissingPaths);

internal static class SvnHttpUpdateReport {
    private const string ProtocolNamespace = "svn:";
    private const int FileChunkSize = 64 * 1024;
    internal static async ValueTask<string?> ReadReportNameAsync(HttpRequest request, long maximumSize, CancellationToken token) {
        request.EnableBuffering();
        try {
            var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximumSize };
            using var reader = XmlReader.Create(request.Body, settings);
            token.ThrowIfCancellationRequested();
            return await reader.MoveToContentAsync().ConfigureAwait(false) == XmlNodeType.Element ? reader.LocalName : null;
        } finally { request.Body.Position = 0; }
    }


    internal static async ValueTask<SvnHttpUpdateRequest> ReadAsync(HttpRequest request, long maximumSize, CancellationToken token) {
        if (request.ContentLength > maximumSize) throw new BadHttpRequestException("The REPORT body is too large.", StatusCodes.Status413PayloadTooLarge);
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximumSize };
        using var reader = XmlReader.Create(request.Body, settings);
        if (await reader.MoveToContentAsync().ConfigureAwait(false) != XmlNodeType.Element ||
            reader.NamespaceURI != ProtocolNamespace || reader.LocalName != "update-report") {
            throw new BadHttpRequestException("Expected an svn:update-report document.");
        }

        string? source = null;
        string? destination = null;
        SvnRevision? target = null;
        var updateTarget = new SvnRepositoryPath("");
        var depth = "infinity";
        var entries = new List<SvnHttpUpdateEntry>();
        var missing = new HashSet<SvnRepositoryPath>();
        var sendAll = string.Equals(reader.GetAttribute("send-all"), "true", StringComparison.OrdinalIgnoreCase);
        var sendCopyFromArguments = false;
        var textDeltas = true;
        await reader.ReadAsync().ConfigureAwait(false);
        while (!reader.EOF) {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 0) break;
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1 || reader.NamespaceURI != ProtocolNamespace) {
                await reader.ReadAsync().ConfigureAwait(false);
                continue;
            }
            switch (reader.LocalName) {
                case "src-path": source = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false); continue;
                case "dst-path": destination = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false); continue;
                case "target-revision": target = Revision(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false), "target-revision"); continue;
                case "update-target": updateTarget = new(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false)); continue;
                case "depth": depth = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false); continue;
                case "send-copyfrom-args": sendCopyFromArguments = true; await reader.SkipAsync().ConfigureAwait(false); continue;
                case "text-deltas": textDeltas = !string.Equals(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false), "no", StringComparison.OrdinalIgnoreCase); continue;
                case "entry":
                    var revision = Revision(reader.GetAttribute("rev") ?? throw new BadHttpRequestException("An update entry has no revision."), "entry revision");
                    var startEmpty = string.Equals(reader.GetAttribute("start-empty"), "true", StringComparison.OrdinalIgnoreCase);
                    var entryDepth = reader.GetAttribute("depth") ?? "infinity";
                    SvnRepositoryPath? linkPath = reader.GetAttribute("linkpath") is { Length: > 0 } value ? new(value) : null;
                    var lockToken = reader.GetAttribute("lock-token");
                    entries.Add(new(new(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false)), revision, startEmpty, entryDepth, linkPath, lockToken));
                    continue;
                case "missing": missing.Add(new(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false))); continue;
            }
            await reader.ReadAsync().ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(source)) throw new BadHttpRequestException("The update report has no src-path.");
        return new(source, destination, target, updateTarget, depth, sendAll, sendCopyFromArguments, textDeltas, entries, missing);
    }

    internal static SvnRepositoryPath ResolveSourcePath(string source, string repositoryRoot) {
        var path = Uri.TryCreate(source, UriKind.Absolute, out var uri) ? uri.AbsolutePath : source;
        if (path.Contains("%2f", StringComparison.OrdinalIgnoreCase) || path.Contains("%5c", StringComparison.OrdinalIgnoreCase))
            throw new BadHttpRequestException("The update source contains an encoded path separator.");
        path = Uri.UnescapeDataString(path).TrimEnd('/');
        var root = repositoryRoot.TrimEnd('/');
        if (!path.Equals(root, StringComparison.Ordinal) && !path.StartsWith(root + "/", StringComparison.Ordinal))
            throw new BadHttpRequestException("The update source is outside this repository.");
        return new(path.Length == root.Length ? "" : path[(root.Length + 1)..]);
    }

    internal static SvnRepositoryPath NormalizeAnchor(SvnRepositoryPath anchor, SvnRepositoryPath target) {
        if (target.IsRoot) return anchor;
        if (anchor.Value == target.Value) return new("");
        var suffix = "/" + target.Value;
        return anchor.Value.EndsWith(suffix, StringComparison.Ordinal) ? new(anchor.Value[..^suffix.Length]) : anchor;
    }

    internal static async Task WriteAsync(HttpResponse response, ISvnRepository repository, SvnHttpUpdateRequest request, SvnRevision targetRevision,
        SvnRepositoryPath baseAnchor, SvnRepositoryPath targetAnchor, string revisionRootStub, SvnDiffVersion svndiffVersion, CancellationToken token) {
        var rootEntry = request.Entries.FirstOrDefault(entry => entry.Path.IsRoot) ?? new(new(""), new(0), true, request.Depth, null);
        var baseRoot = await repository.OpenRevisionAsync(rootEntry.Revision, token).ConfigureAwait(false);
        var targetRoot = await repository.OpenRevisionAsync(targetRevision, token).ConfigureAwait(false);
        var targetTree = await ReadTreeAsync(targetRoot, targetAnchor, token).ConfigureAwait(false);
        await ApplyRepositoryLocksAsync(repository, targetTree, targetAnchor, request.Entries, token).ConfigureAwait(false);
        var baseTree = rootEntry.StartEmpty ? EmptyTree(baseRoot, baseAnchor, targetTree[""]) : await ReadTreeAsync(baseRoot, baseAnchor, token).ConfigureAwait(false);
        await ApplyReportOverridesAsync(repository, baseTree, targetTree, request.Entries, request.MissingPaths, baseAnchor, targetRevision, token).ConfigureAwait(false);
        ApplyReportedLocks(baseTree, request.Entries);
        RestrictToScope(baseTree, request.UpdateTarget.Value);
        RestrictToScope(targetTree, request.UpdateTarget.Value);
        RestrictToDepth(baseTree, request.UpdateTarget.Value, request.Depth);
        RestrictToDepth(targetTree, request.UpdateTarget.Value, request.Depth);
        foreach (var entry in request.Entries.Where(entry => !entry.Path.IsRoot && entry.Depth != "infinity")) {
            RestrictToDepth(baseTree, entry.Path.Value, entry.Depth);
            RestrictToDepth(targetTree, entry.Path.Value, entry.Depth);
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/xml; charset=utf-8";
        var settings = new XmlWriterSettings { Async = true, Encoding = new UTF8Encoding(false), CloseOutput = false };
        await using var writer = XmlWriter.Create(response.Body, settings);
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        writer.WriteStartElement("S", "update-report", ProtocolNamespace);
        writer.WriteAttributeString("xmlns", "D", null, SvnDavXml.Dav);
        writer.WriteAttributeString("xmlns", "V", null, SvnDavXml.SvnDav);
        if (request.SendAll) writer.WriteAttributeString("send-all", "true");
        writer.WriteAttributeString("inline-props", "true");
        Empty(writer, "target-revision", ("rev", targetRevision.Value.ToString(CultureInfo.InvariantCulture)));
        writer.WriteStartElement("S", "open-directory", ProtocolNamespace);
        writer.WriteAttributeString("rev", rootEntry.Revision.Value.ToString(CultureInfo.InvariantCulture));
        if (request.UpdateTarget.IsRoot) {
            WriteCheckedIn(writer, targetTree[""], revisionRootStub);
            WritePropertyChanges(writer, baseTree[""].Properties, targetTree[""].Properties);
            WriteEntryProperties(writer, targetTree[""], repository.Id, baseTree[""].LockToken);
        }
        var operations = new List<string>();
        await WriteDirectoryAsync(writer, "", baseTree, targetTree, revisionRootStub, repository, request.SendCopyFromArguments,
            request.TextDeltas, svndiffVersion, request.UpdateTarget.Value, operations, token).ConfigureAwait(false);
        if (response.HttpContext.Items.TryGetValue("SvnFlux.Http.Trace", out var detail))
            response.HttpContext.Items["SvnFlux.Http.Trace"] = detail + "; editor=" + string.Join(", ", operations);
        writer.WriteEndElement();
        writer.WriteEndElement();
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteDirectoryAsync(XmlWriter writer, string directoryPath, IReadOnlyDictionary<string, Node> baseTree,
        IReadOnlyDictionary<string, Node> targetTree, string revisionRootStub, ISvnRepository repository, bool copyFrom, bool textDeltas, SvnDiffVersion version, string scope, List<string> operations, CancellationToken token) {
        var names = Children(baseTree, directoryPath).Concat(Children(targetTree, directoryPath)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var name in names) {
            var path = Join(directoryPath, name);
            if (baseTree.TryGetValue(path, out var oldNode) && (!targetTree.TryGetValue(path, out var newNode) || oldNode.Info.Kind != newNode.Info.Kind)) {
                operations.Add("delete " + path);
                Empty(writer, "delete-entry", ("name", name), ("rev", oldNode.Root.Revision.Value.ToString(CultureInfo.InvariantCulture)));
            }
        }

        foreach (var name in names) {
            token.ThrowIfCancellationRequested();
            var path = Join(directoryPath, name);
            baseTree.TryGetValue(path, out var oldNode);
            if (!targetTree.TryGetValue(path, out var newNode)) continue;
            if (oldNode is not null && oldNode.Info.Kind != newNode.Info.Kind) oldNode = null;
            if (oldNode?.IsReportedTarget == true) continue;
            if (newNode.Info.Kind == SvnNodeKind.Directory) {
                writer.WriteStartElement("S", oldNode is null ? "add-directory" : "open-directory", ProtocolNamespace);
                writer.WriteAttributeString("name", name);
                if (oldNode is not null) writer.WriteAttributeString("rev", oldNode.Root.Revision.Value.ToString(CultureInfo.InvariantCulture));
                else if (copyFrom) await WriteCopyFromAsync(writer, repository, newNode, token).ConfigureAwait(false);
                if (oldNode is null || InScope(path, scope)) {
                    WriteCheckedIn(writer, newNode, revisionRootStub);
                    WritePropertyChanges(writer, oldNode?.Properties ?? SvnPropertyCollection.Empty, newNode.Properties);
                    WriteEntryProperties(writer, newNode, repository.Id, oldNode?.LockToken);
                }
                operations.Add((oldNode is null ? "add-dir " : "open-dir ") + path);
                await WriteDirectoryAsync(writer, path, baseTree, targetTree, revisionRootStub, repository, copyFrom, textDeltas, version, scope, operations, token).ConfigureAwait(false);
                writer.WriteEndElement();
            } else if (oldNode is null || !await NodesEqualAsync(oldNode, newNode, token).ConfigureAwait(false)) {
                operations.Add((oldNode is null ? "add-file " : "open-file ") + path);
                await WriteFileAsync(writer, name, oldNode, newNode, revisionRootStub, repository, copyFrom, textDeltas, version, token).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteFileAsync(XmlWriter writer, string name, Node? oldNode, Node newNode, string revisionRootStub,
        ISvnRepository repository, bool copyFrom, bool textDeltas, SvnDiffVersion version, CancellationToken token) {
        writer.WriteStartElement("S", oldNode is null ? "add-file" : "open-file", ProtocolNamespace);
        writer.WriteAttributeString("name", name);
        if (oldNode is not null) writer.WriteAttributeString("rev", oldNode.Root.Revision.Value.ToString(CultureInfo.InvariantCulture));
        else if (copyFrom) await WriteCopyFromAsync(writer, repository, newNode, token).ConfigureAwait(false);
        WriteCheckedIn(writer, newNode, revisionRootStub);
        WritePropertyChanges(writer, oldNode?.Properties ?? SvnPropertyCollection.Empty, newNode.Properties);
        WriteEntryProperties(writer, newNode, repository.Id, oldNode?.LockToken);
        if (oldNode is not null && await FileContentsEqualAsync(oldNode, newNode, token).ConfigureAwait(false)) {
            writer.WriteEndElement();
            return;
        }

        await using var stream = await newNode.Root.OpenFileAsync(newNode.Path, token).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        if (textDeltas) writer.WriteStartElement("S", "txdelta", ProtocolNamespace);
        var base64 = textDeltas ? new Base64XmlWriter(writer) : null;
        if (base64 is not null) await base64.WriteAsync(SvnDiffEncoder.EncodeHeader(version)).ConfigureAwait(false);
        await using var source = oldNode is null ? null : await oldNode.Root.OpenFileAsync(oldNode.Path, token).ConfigureAwait(false);
        var targetBuffer = new byte[FileChunkSize];
        var sourceBuffer = new byte[FileChunkSize];
        long sourceOffset = 0;
        while (true) {
            var count = await ReadChunkAsync(stream, targetBuffer, token).ConfigureAwait(false);
            if (count == 0) break;
            hash.AppendData(targetBuffer, 0, count);
            if (base64 is null) continue;
            var sourceCount = source is null ? 0 : await ReadChunkAsync(source, sourceBuffer, token).ConfigureAwait(false);
            await base64.WriteAsync(SvnDiffEncoder.EncodeWindow(sourceOffset, sourceBuffer.AsSpan(0, sourceCount),
                targetBuffer.AsSpan(0, count), version)).ConfigureAwait(false);
            sourceOffset += sourceCount;
        }
        if (base64 is not null) {
            await base64.CompleteAsync().ConfigureAwait(false);
            writer.WriteEndElement();
        }
        if (!textDeltas) Empty(writer, "txdelta");
        writer.WriteStartElement("S", "prop", ProtocolNamespace);
        writer.WriteElementString("V", "md5-checksum", SvnDavXml.SvnDav, Convert.ToHexStringLower(hash.GetHashAndReset()));
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static async ValueTask WriteCopyFromAsync(XmlWriter writer, ISvnRepository repository, Node node, CancellationToken token) {
        SvnChangedPath? exact = null;
        await foreach (var entry in repository.GetLogAsync(new([node.Path], node.Info.LastChangedRevision, node.Info.LastChangedRevision), token).ConfigureAwait(false)) {
            exact = entry.ChangedPaths.FirstOrDefault(change => change.Path == node.Path);
            if (exact is not null) break;
        }
        if (exact?.CopyFromPath is not { } path || exact.CopyFromRevision is not { } revision) return;
        writer.WriteAttributeString("copyfrom-path", "/" + path.Value);
        writer.WriteAttributeString("copyfrom-rev", revision.Value.ToString(CultureInfo.InvariantCulture));
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

    private static void WriteCheckedIn(XmlWriter writer, Node node, string stub) {
        writer.WriteStartElement("D", "checked-in", SvnDavXml.Dav);
        writer.WriteElementString("D", "href", SvnDavXml.Dav, stub + "/" + node.Info.LastChangedRevision.Value +
            (node.Path.IsRoot ? "" : "/" + string.Join('/', node.Path.Value.Split('/').Select(Uri.EscapeDataString))));
        writer.WriteEndElement();
    }


    private static void WriteEntryProperties(XmlWriter writer, Node node, Guid repositoryId, string? previousLockToken) {
        WriteProperty(writer, "svn:entry:committed-rev", Encoding.UTF8.GetBytes(node.Info.LastChangedRevision.Value.ToString(CultureInfo.InvariantCulture)));
        WriteProperty(writer, "svn:entry:uuid", Encoding.UTF8.GetBytes(repositoryId.ToString()));
        if (node.Info.LastChangedTime is { } time)
            WriteProperty(writer, "svn:entry:committed-date", Encoding.UTF8.GetBytes(time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)));
        if (node.Info.LastChangedAuthor is { } author) WriteProperty(writer, "svn:entry:last-author", Encoding.UTF8.GetBytes(author));
        if (node.LockToken is not null) WriteProperty(writer, "svn:entry:lock-token", Encoding.UTF8.GetBytes(node.LockToken));
        else if (previousLockToken is not null) Empty(writer, "remove-prop", ("name", "svn:entry:lock-token"));
    }
    private static void WritePropertyChanges(XmlWriter writer, SvnPropertyCollection oldProperties, SvnPropertyCollection newProperties) {
        var oldMap = oldProperties.ToDictionary(property => property.Name, StringComparer.Ordinal);
        var newMap = newProperties.ToDictionary(property => property.Name, StringComparer.Ordinal);
        foreach (var name in oldMap.Keys.Concat(newMap.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) {
            oldMap.TryGetValue(name, out var oldProperty);
            newMap.TryGetValue(name, out var newProperty);
            if (oldProperty is not null && newProperty is not null && oldProperty.Value.Span.SequenceEqual(newProperty.Value.Span)) continue;
            if (newProperty is null) Empty(writer, "remove-prop", ("name", name));
            else WriteProperty(writer, name, newProperty.Value.Span);
        }
    }

    private static void WriteProperty(XmlWriter writer, string name, ReadOnlySpan<byte> value) {
        writer.WriteStartElement("S", "set-prop", ProtocolNamespace);
        writer.WriteAttributeString("name", name);
        try {
            var text = new UTF8Encoding(false, true).GetString(value);
            XmlConvert.VerifyXmlChars(text);
            writer.WriteString(text);
        } catch (Exception exception) when (exception is DecoderFallbackException or XmlException) {
            writer.WriteAttributeString("encoding", "base64");
            var bytes = value.ToArray();
            writer.WriteBase64(bytes, 0, bytes.Length);
        }
        writer.WriteEndElement();
    }

    private static async ValueTask<bool> NodesEqualAsync(Node left, Node right, CancellationToken token) {
        if (!PropertiesEqual(left.Properties, right.Properties)) return false;
        if (!string.Equals(left.LockToken, right.LockToken, StringComparison.Ordinal)) return false;
        if (left.Info.LastChangedRevision != right.Info.LastChangedRevision || left.Info.LastChangedTime != right.Info.LastChangedTime ||
            !string.Equals(left.Info.LastChangedAuthor, right.Info.LastChangedAuthor, StringComparison.Ordinal)) return false;
        return await FileContentsEqualAsync(left, right, token).ConfigureAwait(false);
    }

    private static async ValueTask<bool> FileContentsEqualAsync(Node left, Node right, CancellationToken token) {
        await using var first = await left.Root.OpenFileAsync(left.Path, token).ConfigureAwait(false);
        await using var second = await right.Root.OpenFileAsync(right.Path, token).ConfigureAwait(false);
        var a = new byte[FileChunkSize];
        var b = new byte[FileChunkSize];
        while (true) {
            var ac = await first.ReadAsync(a, token).ConfigureAwait(false);
            var bc = await second.ReadAsync(b, token).ConfigureAwait(false);
            if (ac != bc || !a.AsSpan(0, ac).SequenceEqual(b.AsSpan(0, bc))) return false;
            if (ac == 0) return true;
        }
    }

    private static bool PropertiesEqual(SvnPropertyCollection left, SvnPropertyCollection right) {
        if (left.Count != right.Count) return false;
        var map = right.ToDictionary(property => property.Name, StringComparer.Ordinal);
        return left.All(property => map.TryGetValue(property.Name, out var other) && property.Value.Span.SequenceEqual(other.Value.Span));
    }

    private static async ValueTask ApplyRepositoryLocksAsync(ISvnRepository repository, Dictionary<string, Node> tree, SvnRepositoryPath anchor,
        IReadOnlyList<SvnHttpUpdateEntry> entries, CancellationToken token) {
        if (repository is not ISvnWritableRepository writable) return;
        foreach (var entry in entries.Where(entry => entry.LockToken is not null)) {
            var path = entry.Path.IsRoot ? anchor : anchor.Append(entry.Path);
            var current = await writable.GetLockAsync(path, token).ConfigureAwait(false);
            if (current?.Token == entry.LockToken! && tree.TryGetValue(entry.Path.Value, out var node))
                tree[entry.Path.Value] = node with { LockToken = current.Token };
        }
    }

    private static void ApplyReportedLocks(Dictionary<string, Node> tree, IReadOnlyList<SvnHttpUpdateEntry> entries) {
        foreach (var entry in entries) {
            if (entry.LockToken is null || !tree.TryGetValue(entry.Path.Value, out var node)) continue;
            tree[entry.Path.Value] = node with { LockToken = entry.LockToken };
        }
    }

    private static async ValueTask<Dictionary<string, Node>> ReadTreeAsync(ISvnRevisionRoot root, SvnRepositoryPath anchor, CancellationToken token) {
        var info = await root.GetNodeInfoAsync(anchor, token).ConfigureAwait(false) ?? throw new SvnPathNotFoundException(anchor);
        if (info.Kind != SvnNodeKind.Directory) throw new SvnNodeKindMismatchException(anchor, SvnNodeKind.Directory);
        var result = new Dictionary<string, Node>(StringComparer.Ordinal);
        await ReadNodeAsync(root, "", anchor, info, result, token).ConfigureAwait(false);
        return result;
    }

    private static async ValueTask ReadNodeAsync(ISvnRevisionRoot root, string relative, SvnRepositoryPath path, SvnNodeInfo info,
        Dictionary<string, Node> result, CancellationToken token) {
        result.Add(relative, new(root, path, info, await root.GetPropertiesAsync(path, token).ConfigureAwait(false)));
        if (info.Kind != SvnNodeKind.Directory) return;
        await foreach (var entry in root.GetDirectoryAsync(path, token).ConfigureAwait(false)) {
            await ReadNodeAsync(root, Join(relative, entry.Name), path.Append(new(entry.Name)), entry.NodeInfo, result, token).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, Node> EmptyTree(ISvnRevisionRoot root, SvnRepositoryPath baseAnchor, Node targetRoot) =>
        new(StringComparer.Ordinal) { [""] = new(root, baseAnchor, targetRoot.Info, SvnPropertyCollection.Empty) };

    private static async ValueTask ApplyReportOverridesAsync(ISvnRepository repository, Dictionary<string, Node> baseTree,
        IReadOnlyDictionary<string, Node> targetTree, IReadOnlyList<SvnHttpUpdateEntry> entries, IReadOnlySet<SvnRepositoryPath> missingPaths,
        SvnRepositoryPath baseAnchor, SvnRevision targetRevision, CancellationToken token) {
        foreach (var entry in entries.Where(entry => !entry.Path.IsRoot)) {
            if (entry.StartEmpty) { RemoveSubtree(baseTree, entry.Path.Value); continue; }
            var root = await repository.OpenRevisionAsync(entry.Revision, token).ConfigureAwait(false);
            var reportedTree = await ReadTreeAsync(root, baseAnchor, token).ConfigureAwait(false);
            ReplaceSubtree(baseTree, reportedTree, entry.Path.Value, entry.Revision == targetRevision);
        }
        foreach (var path in missingPaths) ReplaceSubtree(baseTree, targetTree, path.Value, true);
    }

    private static void ReplaceSubtree(Dictionary<string, Node> destination, IReadOnlyDictionary<string, Node> source, string path, bool reportedTarget) {
        RemoveSubtree(destination, path);
        foreach (var pair in source.Where(pair => pair.Key == path || pair.Key.StartsWith(path + "/", StringComparison.Ordinal)))
            destination[pair.Key] = pair.Value with { IsReportedTarget = reportedTarget };
    }

    private static void RemoveSubtree(Dictionary<string, Node> tree, string path) {
        foreach (var key in tree.Keys.Where(key => key == path || key.StartsWith(path + "/", StringComparison.Ordinal)).ToArray()) tree.Remove(key);
    }

    private static void RestrictToScope(Dictionary<string, Node> tree, string scope) {
        if (scope.Length == 0) return;
        foreach (var key in tree.Keys.Where(key => key.Length != 0 && key != scope && !key.StartsWith(scope + "/", StringComparison.Ordinal) &&
            !scope.StartsWith(key + "/", StringComparison.Ordinal)).ToArray()) tree.Remove(key);
    }

    private static void RestrictToDepth(Dictionary<string, Node> tree, string scope, string depth) {
        if (depth == "infinity") return;
        if (depth == "exclude") { RemoveSubtree(tree, scope); return; }
        var prefix = scope.Length == 0 ? "" : scope + "/";
        foreach (var key in tree.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray()) {
            var relative = key[prefix.Length..];
            if (relative.Length == 0) continue;
            var immediate = !relative.Contains('/');
            var keep = depth switch {
                "empty" => false,
                "files" => immediate && tree[key].Info.Kind == SvnNodeKind.File,
                "immediates" => immediate,
                _ => true
            };
            if (!keep) tree.Remove(key);
        }
    }
    private static bool InScope(string path, string scope) => scope.Length == 0 || path == scope || path.StartsWith(scope + "/", StringComparison.Ordinal);


    private static IEnumerable<string> Children(IReadOnlyDictionary<string, Node> tree, string parent) {
        var prefix = parent.Length == 0 ? "" : parent + "/";
        return tree.Keys.Where(path => path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(path => path[prefix.Length..]).Where(path => !path.Contains('/'));
    }

    private static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;
    private static SvnRevision Revision(string value, string field) {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0)
            throw new BadHttpRequestException($"The {field} value is invalid.");
        return new(revision);
    }

    private static void Empty(XmlWriter writer, string element, params (string Name, string Value)[] attributes) {
        writer.WriteStartElement("S", element, ProtocolNamespace);
        foreach (var attribute in attributes) writer.WriteAttributeString(attribute.Name, attribute.Value);
        writer.WriteEndElement();
    }

    private sealed record Node(ISvnRevisionRoot Root, SvnRepositoryPath Path, SvnNodeInfo Info, SvnPropertyCollection Properties, bool IsReportedTarget = false, string? LockToken = null);

    private sealed class Base64XmlWriter {
        private readonly XmlWriter _writer;
        private readonly byte[] _carry = new byte[2];
        private int _carryCount;
        public Base64XmlWriter(XmlWriter writer) { _writer = writer; }

        public async ValueTask WriteAsync(byte[] value) {
            var offset = 0;
            if (_carryCount > 0) {
                var needed = 3 - _carryCount;
                if (value.Length < needed) { value.CopyTo(_carry, _carryCount); _carryCount += value.Length; return; }
                var group = new byte[3];
                _carry.AsSpan(0, _carryCount).CopyTo(group);
                value.AsSpan(0, needed).CopyTo(group.AsSpan(_carryCount));
                await _writer.WriteRawAsync(Convert.ToBase64String(group)).ConfigureAwait(false);
                offset = needed;
                _carryCount = 0;
            }
            var length = (value.Length - offset) / 3 * 3;
            if (length > 0) await _writer.WriteRawAsync(Convert.ToBase64String(value, offset, length)).ConfigureAwait(false);
            var remainder = value.Length - offset - length;
            if (remainder > 0) value.AsSpan(offset + length, remainder).CopyTo(_carry);
            _carryCount = remainder;
        }

        public async ValueTask CompleteAsync() {
            if (_carryCount > 0) await _writer.WriteRawAsync(Convert.ToBase64String(_carry, 0, _carryCount)).ConfigureAwait(false);
            _carryCount = 0;
        }
    }
}

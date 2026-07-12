using System.Globalization;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Http;
using SvnFlux.Core;

namespace SvnFlux.Http;

internal static class SvnHttpMergeInfoReport {
    private const string ProtocolNamespace = "svn:";

    internal static async Task WriteAsync(HttpRequest request, HttpResponse response, ISvnRepository repository,
        SvnRepositoryPath basePath, SvnRevision latest, long maximumSize, CancellationToken token) {
        var query = await ReadAsync(request, latest, maximumSize, token).ConfigureAwait(false);
        var root = await repository.OpenRevisionAsync(query.Revision, token).ConfigureAwait(false);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var relative in query.Paths) {
            var path = relative.IsRoot ? basePath : basePath.Append(relative);
            if (await root.GetNodeInfoAsync(path, token).ConfigureAwait(false) is null) throw new SvnPathNotFoundException(path);
            var inherited = await FindAsync(root, path, query.Inheritance, token).ConfigureAwait(false);
            if (inherited is not null) result[Relative(basePath, path)] = inherited;
            if (query.IncludeDescendants) await AddDescendantsAsync(root, basePath, path, result, token).ConfigureAwait(false);
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/xml; charset=utf-8";
        await using var writer = XmlWriter.Create(response.Body, new() { Async = true, Encoding = new UTF8Encoding(false), CloseOutput = false });
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        writer.WriteStartElement("S", "mergeinfo-report", ProtocolNamespace);
        writer.WriteAttributeString("xmlns", "D", null, SvnDavXml.Dav);
        foreach (var pair in result) {
            writer.WriteStartElement("S", "mergeinfo-item", ProtocolNamespace);
            writer.WriteElementString("S", "mergeinfo-path", ProtocolNamespace, pair.Key);
            writer.WriteElementString("S", "mergeinfo-info", ProtocolNamespace, pair.Value);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private static async ValueTask<string?> FindAsync(ISvnRevisionRoot root, SvnRepositoryPath path, MergeInfoInheritance inheritance, CancellationToken token) {
        var current = inheritance == MergeInfoInheritance.NearestAncestor ? Parent(path) : path;
        while (current is not null) {
            var value = MergeInfo(await root.GetPropertiesAsync(current.Value, token).ConfigureAwait(false));
            if (value is not null) return value;
            if (inheritance == MergeInfoInheritance.Explicit) return null;
            current = Parent(current.Value);
        }
        return null;
    }

    private static async ValueTask AddDescendantsAsync(ISvnRevisionRoot root, SvnRepositoryPath basePath, SvnRepositoryPath path,
        IDictionary<string, string> result, CancellationToken token) {
        if ((await root.GetNodeInfoAsync(path, token).ConfigureAwait(false))?.Kind != SvnNodeKind.Directory) return;
        await foreach (var entry in root.GetDirectoryAsync(path, token).ConfigureAwait(false)) {
            var child = path.IsRoot ? new SvnRepositoryPath(entry.Name) : path.Append(new(entry.Name));
            var value = MergeInfo(await root.GetPropertiesAsync(child, token).ConfigureAwait(false));
            if (value is not null) result[Relative(basePath, child)] = value;
            if (entry.NodeInfo.Kind == SvnNodeKind.Directory) await AddDescendantsAsync(root, basePath, child, result, token).ConfigureAwait(false);
        }
    }

    private static string? MergeInfo(SvnPropertyCollection properties) {
        var property = properties.FirstOrDefault(value => value.Name == "svn:mergeinfo");
        return property is null ? null : Encoding.UTF8.GetString(property.Value.Span);
    }

    private static string Relative(SvnRepositoryPath parent, SvnRepositoryPath path) {
        if (parent.IsRoot) return path.Value;
        if (path == parent) return "";
        return path.Value[(parent.Value.Length + 1)..];
    }

    private static SvnRepositoryPath? Parent(SvnRepositoryPath path) {
        if (path.IsRoot) return null;
        var separator = path.Value.LastIndexOf('/');
        return separator < 0 ? new SvnRepositoryPath("") : new(path.Value[..separator]);
    }

    private static async ValueTask<MergeInfoRequest> ReadAsync(HttpRequest request, SvnRevision latest, long maximumSize, CancellationToken token) {
        if (request.ContentLength > maximumSize) throw new BadHttpRequestException("The mergeinfo REPORT body is too large.", StatusCodes.Status413PayloadTooLarge);
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximumSize };
        using var reader = XmlReader.Create(request.Body, settings);
        if (await reader.MoveToContentAsync().ConfigureAwait(false) != XmlNodeType.Element || reader.LocalName != "mergeinfo-report" || reader.NamespaceURI != ProtocolNamespace)
            throw new BadHttpRequestException("Expected an svn:mergeinfo-report document.");
        var revision = latest;
        var inheritance = MergeInfoInheritance.Explicit;
        var descendants = false;
        var paths = new List<SvnRepositoryPath>();
        await reader.ReadAsync().ConfigureAwait(false);
        while (!reader.EOF) {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 0) break;
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1 || reader.NamespaceURI != ProtocolNamespace) {
                await reader.ReadAsync().ConfigureAwait(false);
                continue;
            }
            switch (reader.LocalName) {
                case "revision": revision = Revision(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false)); continue;
                case "inherit": inheritance = Inheritance(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false)); continue;
                case "include-descendants": descendants = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false) == "yes"; continue;
                case "path": paths.Add(new(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false))); continue;
            }
            await reader.ReadAsync().ConfigureAwait(false);
        }
        return new(revision, inheritance, descendants, paths.Count == 0 ? [new("")] : paths);
    }

    private static SvnRevision Revision(string value) {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0)
            throw new BadHttpRequestException("The mergeinfo revision is invalid.");
        return new(revision);
    }

    private static MergeInfoInheritance Inheritance(string value) => value switch {
        "explicit" => MergeInfoInheritance.Explicit,
        "inherited" => MergeInfoInheritance.Inherited,
        "nearest-ancestor" => MergeInfoInheritance.NearestAncestor,
        _ => throw new BadHttpRequestException("The mergeinfo inheritance mode is invalid.")
    };

    private enum MergeInfoInheritance { Explicit, Inherited, NearestAncestor }
    private sealed record MergeInfoRequest(SvnRevision Revision, MergeInfoInheritance Inheritance, bool IncludeDescendants, IReadOnlyList<SvnRepositoryPath> Paths);
}

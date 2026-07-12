using System.Globalization;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Http;
using SvnFlux.Core;

namespace SvnFlux.Http;

internal sealed record SvnHttpLogRequest(
    SvnRevision? StartRevision,
    SvnRevision? EndRevision,
    int Limit,
    bool DiscoverChangedPaths,
    bool EncodeBinaryProperties,
    bool AllRevisionProperties,
    IReadOnlySet<string> RevisionProperties,
    IReadOnlyList<SvnRepositoryPath> Paths);

internal static class SvnHttpLogReport {
    private const string ProtocolNamespace = "svn:";
    private static readonly string[] StandardRevisionProperties = ["svn:author", "svn:date", "svn:log"];

    internal static async ValueTask<SvnHttpLogRequest> ReadAsync(HttpRequest request, long maximumSize, CancellationToken token) {
        if (request.ContentLength > maximumSize) throw new BadHttpRequestException("The REPORT body is too large.", StatusCodes.Status413PayloadTooLarge);
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximumSize };
        using var reader = XmlReader.Create(request.Body, settings);
        if (await reader.MoveToContentAsync().ConfigureAwait(false) != XmlNodeType.Element ||
            reader.NamespaceURI != ProtocolNamespace || reader.LocalName != "log-report") {
            throw new BadHttpRequestException("Expected an svn:log-report document.");
        }

        SvnRevision? start = null;
        SvnRevision? end = null;
        var limit = 0;
        var discover = false;
        var encode = false;
        var allProperties = false;
        var sawPropertySelection = false;
        var properties = new HashSet<string>(StringComparer.Ordinal);
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
                case "start-revision": start = Revision(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false), "start-revision"); continue;
                case "end-revision": end = Revision(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false), "end-revision"); continue;
                case "limit":
                    if (!int.TryParse(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false), NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 0)
                        throw new BadHttpRequestException("The log limit is invalid.");
                    continue;
                case "discover-changed-paths": discover = true; break;
                case "encode-binary-props": encode = true; break;
                case "all-revprops": allProperties = sawPropertySelection = true; break;
                case "no-revprops": sawPropertySelection = true; properties.Clear(); break;
                case "revprop": sawPropertySelection = true; properties.Add(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false)); continue;
                case "path": paths.Add(new(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false))); continue;
            }
            await reader.ReadAsync().ConfigureAwait(false);
        }
        if (!sawPropertySelection) properties.UnionWith(StandardRevisionProperties);
        return new(start, end, limit, discover, encode, allProperties, properties, paths);
    }

    internal static async Task WriteAsync(HttpResponse response, IAsyncEnumerable<SvnLogEntry> entries, SvnHttpLogRequest request, CancellationToken token) {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/xml; charset=utf-8";
        var settings = new XmlWriterSettings { Async = true, Encoding = new UTF8Encoding(false), CloseOutput = false };
        await using var writer = XmlWriter.Create(response.Body, settings);
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        writer.WriteStartElement("S", "log-report", ProtocolNamespace);
        writer.WriteAttributeString("xmlns", "D", null, SvnDavXml.Dav);
        await foreach (var entry in entries.WithCancellation(token).ConfigureAwait(false)) {
            writer.WriteStartElement("S", "log-item", ProtocolNamespace);
            if (request.DiscoverChangedPaths) foreach (var change in entry.ChangedPaths) WriteChangedPath(writer, change);
            writer.WriteElementString("D", "version-name", SvnDavXml.Dav, entry.Revision.Value.ToString(CultureInfo.InvariantCulture));
            WriteRevisionProperties(writer, entry.RevisionProperties, request);
            writer.WriteEndElement();
            await writer.FlushAsync().ConfigureAwait(false);
        }
        writer.WriteEndElement();
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static SvnRevision Revision(string value, string element) {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0)
            throw new BadHttpRequestException($"The {element} value is invalid.");
        return new(revision);
    }

    private static void WriteChangedPath(XmlWriter writer, SvnChangedPath change) {
        var element = change.Action switch {
            SvnChangeAction.Add => "added-path",
            SvnChangeAction.Delete => "deleted-path",
            SvnChangeAction.Replace => "replaced-path",
            _ => "modified-path"
        };
        writer.WriteStartElement("S", element, ProtocolNamespace);
        if (change.CopyFromPath is { } copyPath && change.CopyFromRevision is { } copyRevision) {
            writer.WriteAttributeString("copyfrom-path", Absolute(copyPath));
            writer.WriteAttributeString("copyfrom-rev", copyRevision.Value.ToString(CultureInfo.InvariantCulture));
        }
        writer.WriteAttributeString("node-kind", change.NodeKind switch { SvnNodeKind.File => "file", SvnNodeKind.Directory => "dir", _ => "unknown" });
        writer.WriteAttributeString("text-mods", change.TextModified ? "true" : "false");
        writer.WriteAttributeString("prop-mods", change.PropertiesModified ? "true" : "false");
        writer.WriteString(Absolute(change.Path));
        writer.WriteEndElement();
    }

    private static void WriteRevisionProperties(XmlWriter writer, SvnRevisionProperties properties, SvnHttpLogRequest request) {
        if (properties.Author is not null && Wanted(request, "svn:author")) WriteValue(writer, "D", "creator-displayname", SvnDavXml.Dav, Encoding.UTF8.GetBytes(properties.Author), null, request.EncodeBinaryProperties);
        if (properties.Date is { } date && Wanted(request, "svn:date"))
            WriteValue(writer, "S", "date", ProtocolNamespace, Encoding.UTF8.GetBytes(date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)), null, request.EncodeBinaryProperties);
        if (properties.LogMessage is not null && Wanted(request, "svn:log")) WriteValue(writer, "D", "comment", SvnDavXml.Dav, Encoding.UTF8.GetBytes(properties.LogMessage), null, request.EncodeBinaryProperties);
        foreach (var property in properties.CustomProperties) {
            if (Wanted(request, property.Name)) WriteValue(writer, "S", "revprop", ProtocolNamespace, property.Value.Span, property.Name, request.EncodeBinaryProperties);
        }
    }

    private static bool Wanted(SvnHttpLogRequest request, string name) => request.AllRevisionProperties || request.RevisionProperties.Contains(name);

    private static void WriteValue(XmlWriter writer, string prefix, string element, string ns, ReadOnlySpan<byte> bytes, string? name, bool encodeBinary) {
        writer.WriteStartElement(prefix, element, ns);
        if (name is not null) writer.WriteAttributeString("name", name);
        if (TryXmlText(bytes, out var text)) writer.WriteString(text);
        else {
            if (!encodeBinary) throw new InvalidOperationException($"Revision property '{name ?? element}' is not XML-safe.");
            writer.WriteAttributeString("encoding", "base64");
            var value = bytes.ToArray();
            writer.WriteBase64(value, 0, value.Length);
        }
        writer.WriteEndElement();
    }

    private static bool TryXmlText(ReadOnlySpan<byte> bytes, out string text) {
        try {
            text = new UTF8Encoding(false, true).GetString(bytes);
            XmlConvert.VerifyXmlChars(text);
            return true;
        } catch (Exception exception) when (exception is DecoderFallbackException or XmlException) {
            text = "";
            return false;
        }
    }

    private static string Absolute(SvnRepositoryPath path) => path.IsRoot ? "/" : "/" + path.Value;
}

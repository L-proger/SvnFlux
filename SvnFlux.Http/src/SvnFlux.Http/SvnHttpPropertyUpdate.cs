using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Http;

namespace SvnFlux.Http;

internal sealed record SvnHttpPropertyChange(string Name, byte[]? Value, bool HasExpectedValue = false, byte[]? ExpectedValue = null);

internal static class SvnHttpPropertyUpdate {
    internal static async ValueTask<IReadOnlyList<SvnHttpPropertyChange>> ReadAsync(HttpRequest request, long maximumSize, CancellationToken token) {
        if (request.ContentLength > maximumSize) throw new BadHttpRequestException("The PROPPATCH body is too large.", StatusCodes.Status413PayloadTooLarge);
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximumSize };
        using var reader = XmlReader.Create(request.Body, settings);
        if (await reader.MoveToContentAsync().ConfigureAwait(false) != XmlNodeType.Element ||
            reader.NamespaceURI != SvnDavXml.Dav || reader.LocalName != "propertyupdate")
            throw new BadHttpRequestException("Expected a DAV:propertyupdate document.");

        var changes = new List<SvnHttpPropertyChange>();
        bool? set = null;
        var inProperties = false;
        while (await reader.ReadAsync().ConfigureAwait(false)) {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == SvnDavXml.Dav && reader.LocalName is "set" or "remove") {
                set = reader.LocalName == "set";
                continue;
            }
            if (reader.NodeType == XmlNodeType.EndElement && reader.NamespaceURI == SvnDavXml.Dav && reader.LocalName is "set" or "remove") {
                set = null;
                continue;
            }
            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == SvnDavXml.Dav && reader.LocalName == "prop") {
                inProperties = true;
                continue;
            }
            if (reader.NodeType == XmlNodeType.EndElement && reader.NamespaceURI == SvnDavXml.Dav && reader.LocalName == "prop") {
                inProperties = false;
                continue;
            }
            if (!inProperties || set is null || reader.NodeType != XmlNodeType.Element) continue;
            var name = PropertyName(reader.NamespaceURI, reader.LocalName);
            changes.Add(await ReadChangeAsync(reader, set.Value, name).ConfigureAwait(false));
        }
        return changes;
    }

    private static async ValueTask<SvnHttpPropertyChange> ReadChangeAsync(XmlReader reader, bool set, string name) {
        if (!set) {
            if (!reader.IsEmptyElement) await reader.SkipAsync().ConfigureAwait(false);
            return new(name, null);
        }
        var encoding = reader.GetAttribute("encoding", SvnDavXml.SvnDav);
        var absent = reader.GetAttribute("absent", SvnDavXml.SvnDav) == "1";
        if (reader.IsEmptyElement) return new(name, absent ? null : [], false);
        var depth = reader.Depth;
        var text = new StringBuilder();
        byte[]? expected = null;
        var hasExpected = false;
        while (await reader.ReadAsync().ConfigureAwait(false)) {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1 &&
                reader.NamespaceURI == SvnDavXml.SvnDav && reader.LocalName == "old-value") {
                hasExpected = true;
                expected = await ReadValueAsync(reader, name).ConfigureAwait(false);
                continue;
            }
            if (reader.Depth == depth + 1 && reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA) text.Append(reader.Value);
        }
        return new(name, absent ? null : Decode(text.ToString(), encoding, name), hasExpected, expected);
    }

    private static async ValueTask<byte[]?> ReadValueAsync(XmlReader reader, string name) {
        var encoding = reader.GetAttribute("encoding", SvnDavXml.SvnDav);
        var absent = reader.GetAttribute("absent", SvnDavXml.SvnDav) == "1";
        if (reader.IsEmptyElement) return absent ? null : [];
        var depth = reader.Depth;
        var text = new StringBuilder();
        while (await reader.ReadAsync().ConfigureAwait(false)) {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA) text.Append(reader.Value);
        }
        return absent ? null : Decode(text.ToString(), encoding, name);
    }

    private static byte[] Decode(string value, string? encoding, string name) {
        try { return encoding == "base64" ? Convert.FromBase64String(value) : Encoding.UTF8.GetBytes(value); }
        catch (FormatException) { throw new BadHttpRequestException($"Property '{name}' has invalid base64 content."); }
    }

    internal static async Task WriteResponseAsync(HttpResponse response, string href, CancellationToken token, int propertyStatus = StatusCodes.Status200OK) {
        response.StatusCode = StatusCodes.Status207MultiStatus;
        response.ContentType = "text/xml; charset=utf-8";
        var settings = new XmlWriterSettings { Async = true, Encoding = new UTF8Encoding(false), CloseOutput = false };
        await using var writer = XmlWriter.Create(response.Body, settings);
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        writer.WriteStartElement("D", "multistatus", SvnDavXml.Dav);
        writer.WriteStartElement("D", "response", SvnDavXml.Dav);
        writer.WriteElementString("D", "href", SvnDavXml.Dav, href);
        writer.WriteStartElement("D", "propstat", SvnDavXml.Dav);
        writer.WriteStartElement("D", "prop", SvnDavXml.Dav);
        writer.WriteEndElement();
        writer.WriteElementString("D", "status", SvnDavXml.Dav, $"HTTP/1.1 {propertyStatus} {Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(propertyStatus)}");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private static string PropertyName(string namespaceUri, string localName) => namespaceUri switch {
        SvnDavXml.Svn => "svn:" + localName,
        SvnDavXml.Custom => localName,
        _ => localName
    };
}

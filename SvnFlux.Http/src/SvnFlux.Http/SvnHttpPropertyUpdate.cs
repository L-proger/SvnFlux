using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Http;

namespace SvnFlux.Http;

internal sealed record SvnHttpPropertyChange(string Name, byte[]? Value);

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
            var encoded = reader.GetAttribute("encoding", SvnDavXml.SvnDav);
            if (!set.Value) {
                changes.Add(new(name, null));
                if (!reader.IsEmptyElement) await reader.SkipAsync().ConfigureAwait(false);
                continue;
            }
            var text = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
            byte[] value;
            try { value = encoded == "base64" ? Convert.FromBase64String(text) : Encoding.UTF8.GetBytes(text); }
            catch (FormatException) { throw new BadHttpRequestException($"Property '{name}' has invalid base64 content."); }
            changes.Add(new(name, value));
        }
        return changes;
    }

    internal static async Task WriteResponseAsync(HttpResponse response, string href, CancellationToken token) {
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
        writer.WriteElementString("D", "status", SvnDavXml.Dav, "HTTP/1.1 200 OK");
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

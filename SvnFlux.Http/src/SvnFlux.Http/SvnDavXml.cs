using System.Globalization;
using System.Xml;
using Microsoft.AspNetCore.Http;
using SvnFlux.Core;

namespace SvnFlux.Http;

internal static class SvnDavXml {
    internal const string Dav = "DAV:";
    internal const string Svn = "http://subversion.tigris.org/xmlns/svn/";
    internal const string Custom = "http://subversion.tigris.org/xmlns/custom/";
    internal const string SvnDav = "http://subversion.tigris.org/xmlns/dav/";

    private static readonly XmlQualifiedName[] DefaultProperties = [
        new("resourcetype", Dav), new("getcontentlength", Dav), new("getcontenttype", Dav), new("getlastmodified", Dav),
        new("creationdate", Dav), new("getetag", Dav), new("version-name", Dav), new("creator-displayname", Dav),
        new("checked-in", Dav), new("baseline-relative-path", SvnDav), new("repository-uuid", SvnDav), new("deadprop-count", SvnDav)
    ];

    internal static async ValueTask<IReadOnlyList<XmlQualifiedName>> ReadPropertyRequestAsync(HttpRequest request, long maximumSize, CancellationToken token) {
        if (request.ContentLength == 0) return DefaultProperties;
        if (request.ContentLength > maximumSize) throw new BadHttpRequestException("The PROPFIND body is too large.", StatusCodes.Status413PayloadTooLarge);
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximumSize };
        using var reader = XmlReader.Create(request.Body, settings);
        var properties = new List<XmlQualifiedName>();
        var inPropertyList = false;
        while (await reader.ReadAsync().ConfigureAwait(false)) {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == Dav && reader.LocalName == "allprop") return DefaultProperties;
            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == Dav && reader.LocalName == "prop") { inPropertyList = true; continue; }
            if (reader.NodeType == XmlNodeType.EndElement && reader.NamespaceURI == Dav && reader.LocalName == "prop") { inPropertyList = false; continue; }
            if (inPropertyList && reader.NodeType == XmlNodeType.Element) properties.Add(new(reader.LocalName, reader.NamespaceURI));
        }
        return properties.Count == 0 ? DefaultProperties : properties;
    }


    internal static async Task WriteOptionsAsync(HttpResponse response, string activityCollection, CancellationToken token) {
        response.ContentType = "text/xml; charset=utf-8";
        var settings = new XmlWriterSettings { Async = true, Encoding = new System.Text.UTF8Encoding(false), CloseOutput = false };
        await using var writer = XmlWriter.Create(response.Body, settings);
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        writer.WriteStartElement("D", "options-response", Dav);
        writer.WriteStartElement("D", "activity-collection-set", Dav);
        Element(writer, "D", "href", Dav, activityCollection);
        writer.WriteEndElement();
        writer.WriteEndElement();
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }
    internal static async Task WriteNodesAsync(HttpResponse response, IReadOnlyList<SvnDavNode> nodes, IReadOnlyList<XmlQualifiedName> requested,
        Guid repositoryId, string revisionRootStub, CancellationToken token) {
        response.StatusCode = StatusCodes.Status207MultiStatus;
        response.ContentType = "text/xml; charset=utf-8";
        var settings = new XmlWriterSettings { Async = true, Encoding = new System.Text.UTF8Encoding(false), CloseOutput = false };
        await using var writer = XmlWriter.Create(response.Body, settings);
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        writer.WriteStartElement("D", "multistatus", Dav);
        foreach (var node in nodes) {
            token.ThrowIfCancellationRequested();
            writer.WriteStartElement("D", "response", Dav);
            Element(writer, "D", "href", Dav, node.Href);
            writer.WriteStartElement("D", "propstat", Dav);
            writer.WriteStartElement("D", "prop", Dav);
            foreach (var property in requested) WriteNodeProperty(writer, property, node, repositoryId, revisionRootStub);
            writer.WriteEndElement();
            Element(writer, "D", "status", Dav, "HTTP/1.1 200 OK");
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    internal static async Task WriteRevisionAsync(HttpResponse response, string href, SvnRevision revision, SvnRevisionProperties properties,
        IReadOnlyList<XmlQualifiedName> requested, CancellationToken token) {
        response.StatusCode = StatusCodes.Status207MultiStatus;
        response.ContentType = "text/xml; charset=utf-8";
        var settings = new XmlWriterSettings { Async = true, Encoding = new System.Text.UTF8Encoding(false), CloseOutput = false };
        await using var writer = XmlWriter.Create(response.Body, settings);
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        writer.WriteStartElement("D", "multistatus", Dav);
        writer.WriteStartElement("D", "response", Dav); Element(writer, "D", "href", Dav, href);
        writer.WriteStartElement("D", "propstat", Dav); writer.WriteStartElement("D", "prop", Dav);
        foreach (var property in requested) {
            string? value = property.Namespace switch {
                Dav when property.Name == "version-name" => revision.Value.ToString(CultureInfo.InvariantCulture),
                Svn when property.Name == "author" => properties.Author,
                Svn when property.Name == "date" => properties.Date?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                Svn when property.Name == "log" => properties.LogMessage,
                _ => null
            };
            if (value is not null) Element(writer, Prefix(property.Namespace), property.Name, property.Namespace, value);
            else { writer.WriteStartElement(Prefix(property.Namespace), property.Name, property.Namespace); writer.WriteEndElement(); }
        }
        writer.WriteEndElement(); Element(writer, "D", "status", Dav, "HTTP/1.1 200 OK"); writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
        await writer.WriteEndDocumentAsync().ConfigureAwait(false); await writer.FlushAsync().ConfigureAwait(false); token.ThrowIfCancellationRequested();
    }

    private static void WriteNodeProperty(XmlWriter writer, XmlQualifiedName property, SvnDavNode node, Guid repositoryId, string revisionRootStub) {
        var prefix = Prefix(property.Namespace);
        if (property.Namespace == Dav && property.Name == "resourcetype") {
            writer.WriteStartElement(prefix, property.Name, property.Namespace);
            if (node.Info.Kind == SvnNodeKind.Directory) writer.WriteElementString("D", "collection", Dav, "");
            writer.WriteEndElement(); return;
        }
        if (property.Namespace == Dav && property.Name == "checked-in") {
            writer.WriteStartElement(prefix, property.Name, property.Namespace);
            Element(writer, "D", "href", Dav, RevisionHref(revisionRootStub, node.Info.LastChangedRevision, node.Path));
            writer.WriteEndElement(); return;
        }
        var value = property.Namespace switch {
            Dav when property.Name == "getcontentlength" => node.Info.Kind == SvnNodeKind.File ? node.Info.Size.ToString(CultureInfo.InvariantCulture) : "0",
            Dav when property.Name == "getcontenttype" => node.Info.Kind == SvnNodeKind.File ? "application/octet-stream" : "httpd/unix-directory",
            Dav when property.Name == "getlastmodified" => node.Info.LastChangedTime?.UtcDateTime.ToString("R", CultureInfo.InvariantCulture),
            Dav when property.Name == "creationdate" => node.Info.LastChangedTime?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
            Dav when property.Name == "getetag" => $"\"{node.Info.LastChangedRevision.Value}-{node.Path.Value}\"",
            Dav when property.Name == "version-name" => node.Info.LastChangedRevision.Value.ToString(CultureInfo.InvariantCulture),
            Dav when property.Name == "creator-displayname" => node.Info.LastChangedAuthor ?? "",
            SvnDav when property.Name == "baseline-relative-path" => node.Path.Value,
            SvnDav when property.Name == "repository-uuid" => repositoryId.ToString(),
            SvnDav when property.Name == "deadprop-count" => node.Properties.Count.ToString(CultureInfo.InvariantCulture),
            Svn => Property(node.Properties, "svn:" + property.Name),
            Custom => Property(node.Properties, property.Name),
            _ => ""
        };
        Element(writer, prefix, property.Name, property.Namespace, value ?? "");
    }

    private static string RevisionHref(string stub, SvnRevision revision, SvnRepositoryPath path) =>
        stub + "/" + revision.Value + (path.IsRoot ? "" : "/" + string.Join('/', path.Value.Split('/').Select(Uri.EscapeDataString)));
    private static string? Property(SvnPropertyCollection properties, string name) {
        var property = properties.FirstOrDefault(value => value.Name == name);
        return property is null ? null : System.Text.Encoding.UTF8.GetString(property.Value.Span);
    }
    private static string Prefix(string ns) => ns switch { Dav => "D", Svn => "S", Custom => "C", SvnDav => "V", _ => "P" };
    private static void Element(XmlWriter writer, string prefix, string name, string ns, string? value) => writer.WriteElementString(prefix, name, ns, value ?? "");
}

internal sealed record SvnDavNode(string Href, SvnRepositoryPath Path, SvnNodeInfo Info, SvnPropertyCollection Properties);

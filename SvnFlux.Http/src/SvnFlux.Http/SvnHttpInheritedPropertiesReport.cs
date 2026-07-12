using System.Globalization;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Http;
using SvnFlux.Core;

namespace SvnFlux.Http;

internal static class SvnHttpInheritedPropertiesReport {
    private const string ProtocolNamespace = "svn:";

    internal static async Task WriteAsync(HttpRequest request, HttpResponse response, ISvnRepository repository,
        SvnRepositoryPath basePath, SvnRevision latest, long maximumSize, CancellationToken token) {
        var query = await ReadAsync(request, latest, maximumSize, token).ConfigureAwait(false);
        var path = query.Path.IsRoot ? basePath : basePath.Append(query.Path);
        var root = await repository.OpenRevisionAsync(query.Revision, token).ConfigureAwait(false);
        if (await root.GetNodeInfoAsync(path, token).ConfigureAwait(false) is null) throw new SvnPathNotFoundException(path);
        var inherited = new List<(SvnRepositoryPath Path, SvnPropertyCollection Properties)>();
        for (var parent = Parent(path); parent is not null; parent = Parent(parent.Value)) {
            var properties = await root.GetPropertiesAsync(parent.Value, token).ConfigureAwait(false);
            if (properties.Count != 0) inherited.Insert(0, (parent.Value, properties));
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/xml; charset=utf-8";
        await using var writer = XmlWriter.Create(response.Body, new() { Async = true, Encoding = new UTF8Encoding(false), CloseOutput = false });
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        writer.WriteStartElement("S", "inherited-props-report", ProtocolNamespace);
        writer.WriteAttributeString("xmlns", "D", null, SvnDavXml.Dav);
        foreach (var item in inherited) {
            writer.WriteStartElement("S", "iprop-item", ProtocolNamespace);
            writer.WriteElementString("S", "iprop-path", ProtocolNamespace, item.Path.Value);
            foreach (var property in item.Properties) {
                writer.WriteElementString("S", "iprop-propname", ProtocolNamespace, property.Name);
                WriteValue(writer, property.Value.Span);
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private static void WriteValue(XmlWriter writer, ReadOnlySpan<byte> value) {
        writer.WriteStartElement("S", "iprop-propval", ProtocolNamespace);
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

    private static SvnRepositoryPath? Parent(SvnRepositoryPath path) {
        if (path.IsRoot) return null;
        var separator = path.Value.LastIndexOf('/');
        return separator < 0 ? new SvnRepositoryPath("") : new(path.Value[..separator]);
    }

    private static async ValueTask<Request> ReadAsync(HttpRequest request, SvnRevision latest, long maximumSize, CancellationToken token) {
        if (request.ContentLength > maximumSize) throw new BadHttpRequestException("The inherited properties REPORT body is too large.", StatusCodes.Status413PayloadTooLarge);
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximumSize };
        using var reader = XmlReader.Create(request.Body, settings);
        if (await reader.MoveToContentAsync().ConfigureAwait(false) != XmlNodeType.Element || reader.LocalName != "inherited-props-report" || reader.NamespaceURI != ProtocolNamespace)
            throw new BadHttpRequestException("Expected an svn:inherited-props-report document.");
        var revision = latest;
        var path = new SvnRepositoryPath("");
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
                case "path": path = new(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false)); continue;
            }
            await reader.ReadAsync().ConfigureAwait(false);
        }
        return new(revision, path);
    }

    private static SvnRevision Revision(string value) {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0)
            throw new BadHttpRequestException("The inherited properties revision is invalid.");
        return new(revision);
    }

    private sealed record Request(SvnRevision Revision, SvnRepositoryPath Path);
}

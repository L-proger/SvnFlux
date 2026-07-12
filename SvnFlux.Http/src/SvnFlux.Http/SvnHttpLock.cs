using System.Globalization;
using System.Xml;
using Microsoft.AspNetCore.Http;
using SvnFlux.Core;

namespace SvnFlux.Http;

internal static class SvnHttpLock {
    internal static async Task LockAsync(HttpContext context, ISvnRepository repository, SvnHttpResource resource, SvnHttpOptions options) {
        if (resource.Kind != SvnHttpResourceKind.Public || repository is not ISvnWritableRepository writable) { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        var comment = await ReadCommentAsync(context.Request, options.MaximumXmlRequestSize, context.RequestAborted).ConfigureAwait(false);
        var currentRevision = SvnHttpTransaction.ReadBaseRevision(context.Request.Headers["X-SVN-Version-Name"]);
        var steal = context.Request.Headers["X-SVN-Options"].Any(value => value?.Contains("lock-steal", StringComparison.OrdinalIgnoreCase) == true);
        var owner = context.User.Identity?.Name ?? "anonymous";
        var value = await writable.LockAsync(new(resource.Path, owner, comment, steal, currentRevision), context.RequestAborted).ConfigureAwait(false);
        context.Response.Headers["Lock-Token"] = "<" + value.Token + ">";
        context.Response.Headers["X-SVN-Lock-Owner"] = value.Owner;
        context.Response.Headers["X-SVN-Creation-Date"] = value.Created.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        await WriteLockAsync(context.Response, value, context.RequestAborted).ConfigureAwait(false);
    }

    internal static async Task UnlockAsync(HttpContext context, ISvnRepository repository, SvnHttpResource resource) {
        if (resource.Kind != SvnHttpResourceKind.Public || repository is not ISvnWritableRepository writable) { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        var breakLock = context.Request.Headers["X-SVN-Options"].Any(value => value?.Contains("lock-break", StringComparison.OrdinalIgnoreCase) == true);
        var header = context.Request.Headers["Lock-Token"].ToString();
        var token = TryReadToken(header, out var parsed) ? parsed : null;
        if (!breakLock && token is null) throw new BadHttpRequestException("UNLOCK requires a valid Lock-Token header.");
        await writable.UnlockAsync(resource.Path, token, breakLock, context.RequestAborted).ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    internal static bool TryReadToken(string? value, out string token) {
        const string marker = "<opaquelocktoken:";
        var start = value?.IndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
        var end = start < 0 ? -1 : value!.IndexOf('>', start + 1);
        if (start < 0 || end < 0) { token = ""; return false; }
        token = value![(start + 1)..end];
        return true;
    }

    private static async ValueTask<string?> ReadCommentAsync(HttpRequest request, long maximumSize, CancellationToken token) {
        if (request.ContentLength > maximumSize) throw new BadHttpRequestException("The LOCK body is too large.", StatusCodes.Status413PayloadTooLarge);
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximumSize };
        using var reader = XmlReader.Create(request.Body, settings);
        while (await reader.ReadAsync().ConfigureAwait(false)) {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == SvnDavXml.Dav && reader.LocalName == "owner")
                return await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
        }
        return null;
    }

    internal static void WriteDiscovery(XmlWriter writer, SvnLock? value) {
        writer.WriteStartElement("D", "lockdiscovery", SvnDavXml.Dav);
        if (value is not null) {
            writer.WriteStartElement("D", "activelock", SvnDavXml.Dav);
            writer.WriteStartElement("D", "locktype", SvnDavXml.Dav); writer.WriteElementString("D", "write", SvnDavXml.Dav, ""); writer.WriteEndElement();
            writer.WriteStartElement("D", "lockscope", SvnDavXml.Dav); writer.WriteElementString("D", "exclusive", SvnDavXml.Dav, ""); writer.WriteEndElement();
            writer.WriteElementString("D", "depth", SvnDavXml.Dav, "0");
            writer.WriteElementString("D", "owner", SvnDavXml.Dav, value.Owner);
            writer.WriteElementString("D", "timeout", SvnDavXml.Dav, value.Expires is null ? "Infinite" : "Second-" + Math.Max(0, (long)(value.Expires.Value - DateTimeOffset.UtcNow).TotalSeconds));
            writer.WriteStartElement("D", "locktoken", SvnDavXml.Dav); writer.WriteElementString("D", "href", SvnDavXml.Dav, value.Token); writer.WriteEndElement();
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static async Task WriteLockAsync(HttpResponse response, SvnLock value, CancellationToken token) {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/xml; charset=utf-8";
        var settings = new XmlWriterSettings { Async = true, Encoding = new System.Text.UTF8Encoding(false), CloseOutput = false };
        await using var writer = XmlWriter.Create(response.Body, settings);
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        writer.WriteStartElement("D", "prop", SvnDavXml.Dav);
        WriteDiscovery(writer, value);
        writer.WriteEndElement();
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }
}

using System.Globalization;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Http;
using SvnFlux.Core;

namespace SvnFlux.Http;

internal static class SvnHttpCommit {
    internal static async Task PostAsync(HttpContext context, ISvnRepository repository, SvnHttpResource resource, SvnHttpOptions options, SvnHttpTransactionStore store) {
        if (resource.Kind != SvnHttpResourceKind.Me || repository is not ISvnWritableRepository writable) { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        if (context.Request.ContentLength > 64 * 1024) throw new BadHttpRequestException("The transaction request is too large.", StatusCodes.Status413PayloadTooLarge);
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, false, leaveOpen: true);
        var body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        if (!body.Contains("create-txn", StringComparison.Ordinal)) throw new BadHttpRequestException("Expected a create-txn request.");
        var transaction = await store.CreateAsync(writable, context.User.Identity?.Name, options, context.RequestAborted).ConfigureAwait(false);
        context.Response.Headers.Append("SVN-Txn-Name", transaction.Id);
        context.Response.StatusCode = StatusCodes.Status201Created;
    }

    internal static async Task PropPatchAsync(HttpContext context, ISvnRepository repository, SvnHttpResource resource, SvnHttpOptions options, SvnHttpTransactionStore store) {
        if (resource.Kind is not (SvnHttpResourceKind.Revision or SvnHttpResourceKind.Transaction or SvnHttpResourceKind.TransactionRoot)) { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        var changes = await SvnHttpPropertyUpdate.ReadAsync(context.Request, options.MaximumXmlRequestSize, context.RequestAborted).ConfigureAwait(false);
        if (resource.Kind == SvnHttpResourceKind.Revision) {
            if (repository is not ISvnWritableRepository writable) { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
            try {
                foreach (var change in changes)
                    await writable.ChangeRevisionPropertyAsync(new(resource.Revision!.Value, change.Name,
                        Bytes(change.Value), !change.HasExpectedValue, Bytes(change.ExpectedValue)), context.RequestAborted).ConfigureAwait(false);
            } catch (SvnRevisionPropertyConflictException) {
                await SvnHttpPropertyUpdate.WriteResponseAsync(context.Response, context.Request.Path, context.RequestAborted, StatusCodes.Status412PreconditionFailed).ConfigureAwait(false);
                return;
            }
            context.Items["SvnFlux.Http.Trace"] = "revision-properties=" + string.Join(", ", changes.Select(change => $"{change.Name}:{(change.Value is null ? "delete" : "bytes=" + change.Value.Length)}:expected={change.HasExpectedValue}"));
            await SvnHttpPropertyUpdate.WriteResponseAsync(context.Response, context.Request.Path, context.RequestAborted).ConfigureAwait(false);
            return;
        }
        var transaction = store.Get(repository, resource.TransactionId!);
        if (resource.Kind == SvnHttpResourceKind.TransactionRoot) transaction.AddLockToken(resource.Path, context.Request.Headers["If"]);
        if (resource.Kind == SvnHttpResourceKind.Transaction)
            await transaction.SetRevisionPropertiesAsync(changes, context.RequestAborted).ConfigureAwait(false);
        else
            await transaction.SetNodePropertiesAsync(resource.Path, changes,
                SvnHttpTransaction.ReadBaseRevision(context.Request.Headers["X-SVN-Version-Name"]), context.RequestAborted).ConfigureAwait(false);
        context.Items["SvnFlux.Http.Trace"] = "properties=" + string.Join(", ", changes.Select(change => change.Name));
        await SvnHttpPropertyUpdate.WriteResponseAsync(context.Response, context.Request.Path, context.RequestAborted).ConfigureAwait(false);
    }

    internal static async Task PutAsync(HttpContext context, ISvnRepository repository, SvnHttpResource resource, SvnHttpTransactionStore store) {
        if (resource.Kind != SvnHttpResourceKind.TransactionRoot) { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        var transaction = store.Get(repository, resource.TransactionId!);
        var result = await transaction.PutFileAsync(resource.Path, context.Request, context.RequestAborted).ConfigureAwait(false);
        transaction.AddLockToken(resource.Path, context.Request.Headers["If"]);
        context.Response.Headers.Append("X-SVN-Result-Fulltext-MD5", result.Checksum);
        context.Response.StatusCode = result.Added ? StatusCodes.Status201Created : StatusCodes.Status204NoContent;
    }

    internal static async Task MakeCollectionAsync(HttpContext context, ISvnRepository repository, SvnHttpResource resource, SvnHttpTransactionStore store) {
        if (resource.Kind != SvnHttpResourceKind.TransactionRoot) { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        var transaction = store.Get(repository, resource.TransactionId!);
        await transaction.AddDirectoryAsync(resource.Path, context.RequestAborted).ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status201Created;
    }

    internal static async Task CopyAsync(HttpContext context, ISvnRepository repository, SvnHttpResource source, SvnHttpOptions options,
        SvnHttpTransactionStore store, string repositoryRoot) {
        if (source.Kind != SvnHttpResourceKind.RevisionRoot) { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        var destination = ParseResource(context.Request.Headers["Destination"], repositoryRoot, options.SpecialResourceSegment, "COPY destination");
        if (destination.Kind != SvnHttpResourceKind.TransactionRoot) throw new BadHttpRequestException("The COPY destination is not a transaction resource.");
        var transaction = store.Get(repository, destination.TransactionId!);
        await transaction.CopyAsync(source.Path, source.Revision!.Value, destination.Path, context.RequestAborted).ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status201Created;
    }

    internal static async Task MergeAsync(HttpContext context, ISvnRepository repository, SvnHttpResource resource, SvnHttpOptions options, SvnHttpTransactionStore store, string repositoryRoot) {
        if (resource.Kind != SvnHttpResourceKind.Public || repository is not ISvnWritableRepository) { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        var id = await ReadMergeTransactionAsync(context.Request, repositoryRoot, options.SpecialResourceSegment, options.MaximumXmlRequestSize, context.RequestAborted).ConfigureAwait(false);
        var transaction = store.Get(repository, id);
        var keepLocks = !context.Request.Headers["X-SVN-Options"].Any(value => value?.Contains("release-locks", StringComparison.OrdinalIgnoreCase) == true);
        var result = await transaction.CommitAsync(keepLocks, context.RequestAborted).ConfigureAwait(false);
        try {
            await WriteMergeResponseAsync(context.Response, repositoryRoot, result.Revision, result.Properties, context.RequestAborted).ConfigureAwait(false);
        } finally { await store.RemoveAsync(transaction).ConfigureAwait(false); }
    }

    internal static async Task DeleteAsync(HttpContext context, ISvnRepository repository, SvnHttpResource resource, SvnHttpTransactionStore store) {
        if (resource.Kind == SvnHttpResourceKind.Transaction) {
            var transaction = store.Get(repository, resource.TransactionId!);
            await store.RemoveAsync(transaction).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
        if (resource.Kind != SvnHttpResourceKind.TransactionRoot) { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        var revision = SvnHttpTransaction.ReadBaseRevision(context.Request.Headers["X-SVN-Version-Name"]) ??
            throw new BadHttpRequestException("DELETE requires X-SVN-Version-Name.");
        var pending = store.Get(repository, resource.TransactionId!);
        pending.AddLockToken(resource.Path, context.Request.Headers["If"]);
        await pending.DeleteAsync(resource.Path, revision, context.RequestAborted).ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static ReadOnlyMemory<byte>? Bytes(byte[]? value) =>
        value is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(value);

    private static SvnHttpResource ParseResource(string? value, string repositoryRoot, string specialSegment, string field) {
        if (string.IsNullOrWhiteSpace(value)) throw new BadHttpRequestException($"The {field} is missing.");
        var path = Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.AbsolutePath : value;
        var root = repositoryRoot.TrimEnd('/');
        if (!path.StartsWith(root + "/", StringComparison.Ordinal)) throw new BadHttpRequestException($"The {field} is outside this repository.");
        if (!SvnHttpResource.TryParse(path[(root.Length + 1)..], specialSegment, out var resource))
            throw new BadHttpRequestException($"The {field} is invalid.");
        return resource;
    }

    private static async ValueTask<string> ReadMergeTransactionAsync(HttpRequest request, string repositoryRoot, string specialSegment, long maximumSize, CancellationToken token) {
        if (request.ContentLength > maximumSize) throw new BadHttpRequestException("The MERGE body is too large.", StatusCodes.Status413PayloadTooLarge);
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximumSize };
        using var reader = XmlReader.Create(request.Body, settings);
        while (await reader.ReadAsync().ConfigureAwait(false)) {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != SvnDavXml.Dav || reader.LocalName != "href") continue;
            var href = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
            var path = Uri.TryCreate(href, UriKind.Absolute, out var uri) ? uri.AbsolutePath : href;
            var root = repositoryRoot.TrimEnd('/');
            if (!path.StartsWith(root + "/", StringComparison.Ordinal)) throw new BadHttpRequestException("The MERGE source is outside this repository.");
            var relative = path[(root.Length + 1)..];
            if (SvnHttpResource.TryParse(relative, specialSegment, out var resource) && resource.Kind == SvnHttpResourceKind.Transaction)
                return resource.TransactionId!;
        }
        throw new BadHttpRequestException("The MERGE request has no transaction source.");
    }

    private static async Task WriteMergeResponseAsync(HttpResponse response, string repositoryRoot, SvnRevision revision, SvnRevisionProperties properties, CancellationToken token) {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/xml; charset=utf-8";
        var settings = new XmlWriterSettings { Async = true, Encoding = new UTF8Encoding(false), CloseOutput = false };
        await using var writer = XmlWriter.Create(response.Body, settings);
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        writer.WriteStartElement("D", "merge-response", SvnDavXml.Dav);
        writer.WriteStartElement("D", "updated-set", SvnDavXml.Dav);
        writer.WriteStartElement("D", "response", SvnDavXml.Dav);
        writer.WriteElementString("D", "href", SvnDavXml.Dav, repositoryRoot);
        writer.WriteStartElement("D", "propstat", SvnDavXml.Dav);
        writer.WriteStartElement("D", "prop", SvnDavXml.Dav);
        writer.WriteStartElement("D", "resourcetype", SvnDavXml.Dav);
        writer.WriteStartElement("D", "baseline", SvnDavXml.Dav);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteElementString("D", "version-name", SvnDavXml.Dav, revision.Value.ToString(CultureInfo.InvariantCulture));
        if (properties.Date is { } date) writer.WriteElementString("D", "creationdate", SvnDavXml.Dav, date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
        if (properties.Author is not null) writer.WriteElementString("D", "creator-displayname", SvnDavXml.Dav, properties.Author);
        writer.WriteEndElement();
        writer.WriteElementString("D", "status", SvnDavXml.Dav, "HTTP/1.1 200 OK");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SvnFlux.Core;
using SvnFlux.Svndiff;

namespace SvnFlux.Http;

internal static class SvnHttpServer {
    internal static readonly string[] Methods = ["OPTIONS", "PROPFIND", "REPORT", "GET", "HEAD", "POST", "PROPPATCH", "PUT", "MERGE", "DELETE", "MKCOL", "COPY", "LOCK", "UNLOCK"];

    internal static async Task HandleAsync(HttpContext context, ISvnRepository repository, string? path) {
        string? detail = null;
        var options = context.RequestServices.GetService<IOptions<SvnHttpOptions>>()?.Value ?? new();
        var transactions = context.RequestServices.GetRequiredService<SvnHttpTransactionStore>();
        try {
            if (!SvnHttpResource.TryParse(path, options.SpecialResourceSegment, out var resource)) { context.Response.StatusCode = 400; return; }
            switch (context.Request.Method) {
                case "OPTIONS": await OptionsAsync(context, repository, options).ConfigureAwait(false); break;
                case "PROPFIND": await PropFindAsync(context, repository, resource, options).ConfigureAwait(false); break;
                case "GET": await GetAsync(context, repository, resource, false).ConfigureAwait(false); break;
                case "REPORT": await ReportAsync(context, repository, resource, options).ConfigureAwait(false); break;
                case "HEAD": await GetAsync(context, repository, resource, true).ConfigureAwait(false); break;
                case "POST": await SvnHttpCommit.PostAsync(context, repository, resource, options, transactions).ConfigureAwait(false); break;
                case "PROPPATCH": await SvnHttpCommit.PropPatchAsync(context, repository, resource, options, transactions).ConfigureAwait(false); break;
                case "PUT": await SvnHttpCommit.PutAsync(context, repository, resource, transactions).ConfigureAwait(false); break;
                case "MERGE": await SvnHttpCommit.MergeAsync(context, repository, resource, options, transactions, RepositoryRoot(context)).ConfigureAwait(false); break;
                case "DELETE": await SvnHttpCommit.DeleteAsync(context, repository, resource, options, transactions, RepositoryRoot(context)).ConfigureAwait(false); break;
                case "MKCOL": await SvnHttpCommit.MakeCollectionAsync(context, repository, resource, transactions).ConfigureAwait(false); break;
                case "COPY": await SvnHttpCommit.CopyAsync(context, repository, resource, options, transactions, RepositoryRoot(context)).ConfigureAwait(false); break;
                case "LOCK": await SvnHttpLock.LockAsync(context, repository, resource, options).ConfigureAwait(false); break;
                case "UNLOCK": await SvnHttpLock.UnlockAsync(context, repository, resource).ConfigureAwait(false); break;
                default: context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; break;
            }
        } catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) {
            detail = "request cancelled";
            if (!context.Response.HasStarted) context.Response.StatusCode = 499;
        } catch (Exception exception) {
            detail = exception.GetType().Name + ": " + exception.Message;
            if (context.Response.HasStarted) return;
            if (SvnHttpErrors.TryMap(exception, out var statusCode, out var errorCode))
                await SvnDavXml.WriteErrorAsync(context.Response, statusCode, errorCode, exception.Message, context.RequestAborted).ConfigureAwait(false);
            else
                await SvnDavXml.WriteErrorAsync(context.Response, StatusCodes.Status500InternalServerError, 200000, "Internal server error.", context.RequestAborted).ConfigureAwait(false);
        }
        finally {
            if (detail is null && context.Items.TryGetValue("SvnFlux.Http.Trace", out var traceDetail)) detail = traceDetail?.ToString();
            options.Trace?.Invoke(new(context.Request.Method, context.Request.Path, context.Response.StatusCode, detail));
        }
    }

    private static async Task OptionsAsync(HttpContext context, ISvnRepository repository, SvnHttpOptions options) {
        var revision = await repository.GetLatestRevisionAsync(context.RequestAborted).ConfigureAwait(false);
        var root = RepositoryRoot(context);
        var special = root + "/" + options.SpecialResourceSegment;
        context.Response.Headers.Allow = string.Join(", ", Methods);
        context.Response.Headers.Append("DAV", "1,2");
        context.Response.Headers.Append("MS-Author-Via", "DAV");
        context.Response.Headers.Append("SVN-Youngest-Rev", revision.Value.ToString());
        context.Response.Headers.Append("SVN-Repository-UUID", repository.Id.ToString());
        context.Response.Headers.Append("DAV", SvnDavXml.SvnDav + "svn/log-revprops");
        context.Response.Headers.Append("DAV", SvnDavXml.SvnDav + "svn/atomic-revprops");
        context.Response.Headers.Append("DAV", SvnDavXml.SvnDav + "svn/partial-replay");
        context.Response.Headers.Append("DAV", SvnDavXml.SvnDav + "svn/replay-rev-resource");
        context.Response.Headers.Append("DAV", SvnDavXml.SvnDav + "svn/inherited-props");
        context.Response.Headers.Append("SVN-Repository-Root", root);
        context.Response.Headers.Append("SVN-Me-Resource", special + "/me");
        context.Response.Headers.Append("SVN-Rev-Stub", special + "/rev");
        context.Response.Headers.Append("SVN-Rev-Root-Stub", special + "/rvr");
        context.Response.Headers.Append("SVN-Txn-Stub", special + "/txn");
        context.Response.Headers.Append("SVN-Txn-Root-Stub", special + "/txr");
        context.Response.Headers.Append("SVN-Repository-MergeInfo", "yes");
        context.Response.Headers.Append("SVN-Allow-Bulk-Updates", "Prefer");
        context.Response.StatusCode = StatusCodes.Status200OK;
        await SvnDavXml.WriteOptionsAsync(context.Response, special + "/act", context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task PropFindAsync(HttpContext context, ISvnRepository repository, SvnHttpResource resource, SvnHttpOptions options) {
        var requested = await SvnDavXml.ReadPropertyRequestAsync(context.Request, options.MaximumXmlRequestSize, context.RequestAborted).ConfigureAwait(false);
        if (resource.Kind == SvnHttpResourceKind.Revision) {
            var revision = resource.Revision!.Value;
            var properties = await repository.GetRevisionPropertiesAsync(revision, context.RequestAborted).ConfigureAwait(false);
            context.Items["SvnFlux.Http.Trace"] = "requested=" + string.Join(", ", requested.Select(property => property.Namespace + property.Name)) +
                "; revision-properties=" + string.Join(", ", properties.CustomProperties.Select(property => property.Name));
            var revisionResponseProperties = requested.ToList();
            if (requested.Any(property => property.Namespace == SvnDavXml.SvnDav && property.Name == "deadprop-count")) {
                var standard = new[] {
                    properties.Author is null ? null : new System.Xml.XmlQualifiedName("author", SvnDavXml.Svn),
                    properties.Date is null ? null : new System.Xml.XmlQualifiedName("date", SvnDavXml.Svn),
                    properties.LogMessage is null ? null : new System.Xml.XmlQualifiedName("log", SvnDavXml.Svn)
                };
                foreach (var property in standard) if (property is not null && !revisionResponseProperties.Contains(property)) revisionResponseProperties.Add(property);
                foreach (var property in properties.CustomProperties.Select(value => value.Name.StartsWith("svn:", StringComparison.Ordinal)
                    ? new System.Xml.XmlQualifiedName(value.Name[4..], SvnDavXml.Svn)
                    : new System.Xml.XmlQualifiedName(value.Name, SvnDavXml.Custom)))
                    if (!revisionResponseProperties.Contains(property)) revisionResponseProperties.Add(property);
            }
            await SvnDavXml.WriteRevisionAsync(context.Response, context.Request.Path, revision, properties, revisionResponseProperties, context.RequestAborted).ConfigureAwait(false);
            return;
        }
        if (resource.Kind is not (SvnHttpResourceKind.Public or SvnHttpResourceKind.RevisionRoot)) { context.Response.StatusCode = 405; return; }
        var revisionNumber = resource.Revision ?? await repository.GetLatestRevisionAsync(context.RequestAborted).ConfigureAwait(false);
        var root = await repository.OpenRevisionAsync(revisionNumber, context.RequestAborted).ConfigureAwait(false);
        var node = await ReadNodeAsync(root, resource.Path, context.Request.Path, context.RequestAborted).ConfigureAwait(false);
        if (node is null) { context.Response.StatusCode = 404; return; }
        var locks = resource.Kind == SvnHttpResourceKind.Public ? repository as ISvnWritableRepository : null;
        if (locks is not null) node = node with { Lock = await locks.GetLockAsync(resource.Path, context.RequestAborted).ConfigureAwait(false) };
        context.Items["SvnFlux.Http.Trace"] = "requested=" + string.Join(", ", requested.Select(property => property.Namespace + property.Name)) +
            "; node-properties=" + string.Join(", ", node.Properties.Select(property => property.Name));
        var nodes = new List<SvnDavNode> { node };
        var depth = context.Request.Headers["Depth"].ToString();
        if (depth is not ("0" or "1" or "")) { context.Response.StatusCode = 403; return; }
        if (depth == "1" && node.Info.Kind == SvnNodeKind.Directory) {
            await foreach (var entry in root.GetDirectoryAsync(resource.Path, context.RequestAborted).ConfigureAwait(false)) {
                var childPath = resource.Path.IsRoot ? new SvnRepositoryPath(entry.Name) : new SvnRepositoryPath(resource.Path.Value + "/" + entry.Name);
                var href = context.Request.Path.ToString().TrimEnd('/') + "/" + Uri.EscapeDataString(entry.Name) + (entry.NodeInfo.Kind == SvnNodeKind.Directory ? "/" : "");
                var properties = await root.GetPropertiesAsync(childPath, context.RequestAborted).ConfigureAwait(false);
                var childLock = locks is null ? null : await locks.GetLockAsync(childPath, context.RequestAborted).ConfigureAwait(false);
                nodes.Add(new(href, childPath, entry.NodeInfo, properties, childLock));
            }
        }
        var stub = RepositoryRoot(context) + "/" + options.SpecialResourceSegment + "/rvr";
        var responseProperties = requested.ToList();
        if (requested.Any(property => property.Namespace == SvnDavXml.SvnDav && property.Name == "deadprop-count")) {
            foreach (var property in nodes.SelectMany(value => value.Properties).Select(value =>
                value.Name.StartsWith("svn:", StringComparison.Ordinal)
                    ? new System.Xml.XmlQualifiedName(value.Name[4..], SvnDavXml.Svn)
                    : new System.Xml.XmlQualifiedName(value.Name, SvnDavXml.Custom)).Distinct())
                if (!responseProperties.Contains(property)) responseProperties.Add(property);
        }
        await SvnDavXml.WriteNodesAsync(context.Response, nodes, responseProperties, repository.Id, stub, context.RequestAborted).ConfigureAwait(false);
    }


    private static async Task ReportAsync(HttpContext context, ISvnRepository repository, SvnHttpResource resource, SvnHttpOptions options) {
        var latest = await repository.GetLatestRevisionAsync(context.RequestAborted).ConfigureAwait(false);
        var reportName = resource.Kind == SvnHttpResourceKind.Me ? "update-report" :
            await SvnHttpUpdateReport.ReadReportNameAsync(context.Request, options.MaximumXmlRequestSize, context.RequestAborted).ConfigureAwait(false);
        if (reportName == "update-report") {
            var request = await SvnHttpUpdateReport.ReadAsync(context.Request, options.MaximumXmlRequestSize, context.RequestAborted).ConfigureAwait(false);
            var root = RepositoryRoot(context);
            var baseAnchor = SvnHttpUpdateReport.NormalizeAnchor(SvnHttpUpdateReport.ResolveSourcePath(request.SourcePath, root), request.UpdateTarget);
            context.Items["SvnFlux.Http.Trace"] = $"update target='{request.UpdateTarget.Value}', depth='{request.Depth}', send-all={request.SendAll}, text-deltas={request.TextDeltas}, entries=" +
                string.Join(", ", request.Entries.Select(entry => $"'{entry.Path.Value}'@{entry.Revision.Value}:{entry.Depth}{(entry.StartEmpty ? ":empty" : "")}")) +
                (request.MissingPaths.Count == 0 ? "" : "; missing=" + string.Join(", ", request.MissingPaths.Select(path => $"'{path.Value}'")));
            var targetAnchor = request.DestinationPath is null ? baseAnchor : SvnHttpUpdateReport.NormalizeAnchor(SvnHttpUpdateReport.ResolveSourcePath(request.DestinationPath, root), request.UpdateTarget);
            var stub = root + "/" + options.SpecialResourceSegment + "/rvr";
            var version = context.Request.Headers["DAV"].Any(value => value?.Contains("svn/svndiff1", StringComparison.OrdinalIgnoreCase) == true)
                ? SvnDiffVersion.One : SvnDiffVersion.Zero;
            await SvnHttpUpdateReport.WriteAsync(context.Response, repository, request, request.TargetRevision ?? latest,
                baseAnchor, targetAnchor, stub, version, context.RequestAborted).ConfigureAwait(false);
            return;
        }
        if (reportName == "log-report" && resource.Kind == SvnHttpResourceKind.RevisionRoot) {
            var request = await SvnHttpLogReport.ReadAsync(context.Request, options.MaximumXmlRequestSize, context.RequestAborted).ConfigureAwait(false);
            var paths = request.Paths.Count == 0 ? [resource.Path] : request.Paths.Select(resource.Path.Append).ToArray();
            var query = new SvnLogQuery(paths, request.StartRevision ?? latest, request.EndRevision ?? latest, request.Limit);
            await SvnHttpLogReport.WriteAsync(context.Response, repository.GetLogAsync(query, context.RequestAborted), request, context.RequestAborted).ConfigureAwait(false);
            return;
        }
        if (reportName == "get-location-segments") {
            await SvnHttpAuxiliaryReports.WriteLocationSegmentsAsync(context.Request, context.Response, repository, resource.Path,
                latest, options.MaximumXmlRequestSize, context.RequestAborted).ConfigureAwait(false);
            return;
        }
        if (reportName == "get-locks-report") {
            if (repository is not ISvnWritableRepository writable) { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
            await SvnHttpAuxiliaryReports.WriteLocksAsync(context.Response, writable, resource.Path, context.RequestAborted).ConfigureAwait(false);
            return;
        }
        if (reportName == "get-locations") {
            await SvnHttpAuxiliaryReports.WriteLocationsAsync(context.Request, context.Response, repository, resource.Path,
                options.MaximumXmlRequestSize, context.RequestAborted).ConfigureAwait(false);
            return;
        }
        if (reportName == "file-revs-report") {
            var version = context.Request.Headers["DAV"].Any(value => value?.Contains("svn/svndiff1", StringComparison.OrdinalIgnoreCase) == true)
                ? SvnDiffVersion.One : SvnDiffVersion.Zero;
            await SvnHttpAuxiliaryReports.WriteFileRevisionsAsync(context.Request, context.Response, repository, resource.Path,
                version, options.MaximumXmlRequestSize, context.RequestAborted).ConfigureAwait(false);
            return;
        }
        if (reportName == "mergeinfo-report") {
            await SvnHttpMergeInfoReport.WriteAsync(context.Request, context.Response, repository, resource.Path, latest,
                options.MaximumXmlRequestSize, context.RequestAborted).ConfigureAwait(false);
            return;
        }
        if (reportName == "replay-report") {
            var version = context.Request.Headers["DAV"].Any(value => value?.Contains("svn/svndiff1", StringComparison.OrdinalIgnoreCase) == true)
                ? SvnDiffVersion.One : SvnDiffVersion.Zero;
            await SvnHttpReplayReport.WriteAsync(context.Request, context.Response, repository, resource, version,
                options.MaximumXmlRequestSize, context.RequestAborted).ConfigureAwait(false);
            return;
        }
        if (reportName == "inherited-props-report") {
            await SvnHttpInheritedPropertiesReport.WriteAsync(context.Request, context.Response, repository, resource.Path, latest,
                options.MaximumXmlRequestSize, context.RequestAborted).ConfigureAwait(false);
            return;
        }
        context.Response.StatusCode = StatusCodes.Status501NotImplemented;
        context.Items["SvnFlux.Http.Trace"] = "unsupported REPORT '" + reportName + "'";
    }
    private static async Task GetAsync(HttpContext context, ISvnRepository repository, SvnHttpResource resource, bool head) {
        if (resource.Kind is not (SvnHttpResourceKind.Public or SvnHttpResourceKind.RevisionRoot)) { context.Response.StatusCode = 405; return; }
        var revision = resource.Revision ?? await repository.GetLatestRevisionAsync(context.RequestAborted).ConfigureAwait(false);
        var root = await repository.OpenRevisionAsync(revision, context.RequestAborted).ConfigureAwait(false);
        var info = await root.GetNodeInfoAsync(resource.Path, context.RequestAborted).ConfigureAwait(false);
        if (info is null) { context.Response.StatusCode = 404; return; }
        if (info.Kind != SvnNodeKind.File) { context.Response.StatusCode = 405; return; }
        context.Response.StatusCode = 200; context.Response.ContentType = "application/octet-stream"; context.Response.ContentLength = info.Size;
        context.Response.Headers.ETag = $"\"{info.LastChangedRevision.Value}-{resource.Path.Value}\"";
        if (info.LastChangedTime is { } time) context.Response.Headers.LastModified = time.ToString("R");
        if (head) return;
        await using var stream = await root.OpenFileAsync(resource.Path, context.RequestAborted).ConfigureAwait(false);
        await stream.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    private static async ValueTask<SvnDavNode?> ReadNodeAsync(ISvnRevisionRoot root, SvnRepositoryPath path, string href, CancellationToken token) {
        var info = await root.GetNodeInfoAsync(path, token).ConfigureAwait(false);
        if (info is null) return null;
        var properties = await root.GetPropertiesAsync(path, token).ConfigureAwait(false);
        if (info.Kind == SvnNodeKind.Directory && !href.EndsWith('/')) href += "/";
        return new(href, path, info, properties);
    }

    private static string RepositoryRoot(HttpContext context) {
        var routePath = context.Request.RouteValues["svnPath"]?.ToString();
        var path = context.Request.PathBase + context.Request.Path;
        if (string.IsNullOrEmpty(routePath)) return path.ToString().TrimEnd('/');
        return path.ToString()[..^(routePath.Length + 1)].TrimEnd('/');
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SvnFlux.Core;
using SvnFlux.Svndiff;

namespace SvnFlux.Http;

internal static class SvnHttpServer {
    internal static readonly string[] Methods = ["OPTIONS", "PROPFIND", "REPORT", "GET", "HEAD"];

    internal static async Task HandleAsync(HttpContext context, ISvnRepository repository, string? path) {
        string? detail = null;
        var options = context.RequestServices.GetService<IOptions<SvnHttpOptions>>()?.Value ?? new();
        try {
            if (!SvnHttpResource.TryParse(path, options.SpecialResourceSegment, out var resource)) { context.Response.StatusCode = 400; return; }
            switch (context.Request.Method) {
                case "OPTIONS": await OptionsAsync(context, repository, options).ConfigureAwait(false); break;
                case "PROPFIND": await PropFindAsync(context, repository, resource, options).ConfigureAwait(false); break;
                case "GET": await GetAsync(context, repository, resource, false).ConfigureAwait(false); break;
                case "REPORT": await ReportAsync(context, repository, resource, options).ConfigureAwait(false); break;
                case "HEAD": await GetAsync(context, repository, resource, true).ConfigureAwait(false); break;
                default: context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; break;
            }
        } catch (BadHttpRequestException exception) { detail = exception.Message; context.Response.StatusCode = exception.StatusCode; }
        catch (SvnPathNotFoundException exception) { detail = exception.Message; context.Response.StatusCode = StatusCodes.Status404NotFound; }
        catch (SvnInvalidRevisionException exception) { detail = exception.Message; context.Response.StatusCode = StatusCodes.Status404NotFound; }
        catch (Exception exception) { detail = exception.ToString(); context.Response.StatusCode = StatusCodes.Status500InternalServerError; throw; }
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
        context.Response.Headers.Append("SVN-Repository-Root", root);
        context.Response.Headers.Append("SVN-Me-Resource", special + "/me");
        context.Response.Headers.Append("SVN-Rev-Stub", special + "/rev");
        context.Response.Headers.Append("SVN-Rev-Root-Stub", special + "/rvr");
        context.Response.Headers.Append("SVN-Txn-Stub", special + "/txn");
        context.Response.Headers.Append("SVN-Txn-Root-Stub", special + "/txr");
        context.Response.Headers.Append("SVN-Repository-MergeInfo", "no");
        context.Response.Headers.Append("SVN-Allow-Bulk-Updates", "Prefer");
        context.Response.StatusCode = StatusCodes.Status200OK;
        await SvnDavXml.WriteOptionsAsync(context.Response, special + "/act", context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task PropFindAsync(HttpContext context, ISvnRepository repository, SvnHttpResource resource, SvnHttpOptions options) {
        var requested = await SvnDavXml.ReadPropertyRequestAsync(context.Request, options.MaximumXmlRequestSize, context.RequestAborted).ConfigureAwait(false);
        if (resource.Kind == SvnHttpResourceKind.Revision) {
            var revision = resource.Revision!.Value;
            var properties = await repository.GetRevisionPropertiesAsync(revision, context.RequestAborted).ConfigureAwait(false);
            await SvnDavXml.WriteRevisionAsync(context.Response, context.Request.Path, revision, properties, requested, context.RequestAborted).ConfigureAwait(false); return;
        }
        if (resource.Kind is not (SvnHttpResourceKind.Public or SvnHttpResourceKind.RevisionRoot)) { context.Response.StatusCode = 405; return; }
        var revisionNumber = resource.Revision ?? await repository.GetLatestRevisionAsync(context.RequestAborted).ConfigureAwait(false);
        var root = await repository.OpenRevisionAsync(revisionNumber, context.RequestAborted).ConfigureAwait(false);
        var node = await ReadNodeAsync(root, resource.Path, context.Request.Path, context.RequestAborted).ConfigureAwait(false);
        if (node is null) { context.Response.StatusCode = 404; return; }
        var nodes = new List<SvnDavNode> { node };
        var depth = context.Request.Headers["Depth"].ToString();
        if (depth is not ("0" or "1" or "")) { context.Response.StatusCode = 403; return; }
        if (depth == "1" && node.Info.Kind == SvnNodeKind.Directory) {
            await foreach (var entry in root.GetDirectoryAsync(resource.Path, context.RequestAborted).ConfigureAwait(false)) {
                var childPath = resource.Path.IsRoot ? new SvnRepositoryPath(entry.Name) : new SvnRepositoryPath(resource.Path.Value + "/" + entry.Name);
                var href = context.Request.Path.ToString().TrimEnd('/') + "/" + Uri.EscapeDataString(entry.Name) + (entry.NodeInfo.Kind == SvnNodeKind.Directory ? "/" : "");
                var properties = await root.GetPropertiesAsync(childPath, context.RequestAborted).ConfigureAwait(false);
                nodes.Add(new(href, childPath, entry.NodeInfo, properties));
            }
        }
        var stub = RepositoryRoot(context) + "/" + options.SpecialResourceSegment + "/rvr";
        await SvnDavXml.WriteNodesAsync(context.Response, nodes, requested, repository.Id, stub, context.RequestAborted).ConfigureAwait(false);
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
            await SvnHttpAuxiliaryReports.WriteEmptyLocksAsync(context.Response, context.RequestAborted).ConfigureAwait(false);
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

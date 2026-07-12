using System.Globalization;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Http;
using SvnFlux.Core;
using SvnFlux.Svndiff;

namespace SvnFlux.Http;

internal static class SvnHttpAuxiliaryReports {
    private const string ProtocolNamespace = "svn:";

    internal static async Task WriteLocksAsync(HttpResponse response, ISvnWritableRepository repository, SvnRepositoryPath path, CancellationToken token) {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/xml; charset=utf-8";
        await using var writer = XmlWriter.Create(response.Body, Settings());
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        writer.WriteStartElement("S", "get-locks-report", ProtocolNamespace);
        writer.WriteAttributeString("xmlns", "D", null, SvnDavXml.Dav);
        await foreach (var value in repository.GetLocksAsync(path, token).ConfigureAwait(false)) {
            writer.WriteStartElement("S", "lock", ProtocolNamespace);
            writer.WriteElementString("S", "path", ProtocolNamespace, "/" + value.Path.Value);
            writer.WriteElementString("S", "token", ProtocolNamespace, value.Token);
            writer.WriteElementString("S", "creationdate", ProtocolNamespace, value.Created.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            if (value.Expires is { } expires) writer.WriteElementString("S", "expirationdate", ProtocolNamespace, expires.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteElementString("S", "owner", ProtocolNamespace, value.Owner);
            if (value.Comment is not null) writer.WriteElementString("S", "comment", ProtocolNamespace, value.Comment);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    internal static async Task WriteLocationSegmentsAsync(HttpRequest request, HttpResponse response, ISvnRepository repository,
        SvnRepositoryPath basePath, SvnRevision latest, long maximumSize, CancellationToken token) {
        var report = await ReadLocationSegmentsAsync(request, latest, maximumSize, token).ConfigureAwait(false);
        var path = report.Path.IsRoot ? basePath : basePath.Append(report.Path);
        var segments = new List<LocationSegment>();
        var currentPath = path;
        var rangeStart = report.Start.Value;
        while (rangeStart >= report.End.Value) {
            var rangeEnd = rangeStart;
            while (rangeEnd > report.End.Value) {
                var previous = await repository.OpenRevisionAsync(new(rangeEnd - 1), token).ConfigureAwait(false);
                if (await previous.GetNodeInfoAsync(currentPath, token).ConfigureAwait(false) is null) break;
                rangeEnd--;
            }
            segments.Add(new(currentPath.Value, rangeStart, rangeEnd));
            if (rangeEnd == report.End.Value) break;
            var copy = await FindCopySourceAsync(repository, currentPath, new(rangeEnd), token).ConfigureAwait(false);
            if (copy is null) {
                segments.Add(new(null, rangeEnd - 1, report.End.Value));
                break;
            }
            if (copy.Revision.Value < rangeEnd - 1) segments.Add(new(null, rangeEnd - 1, copy.Revision.Value + 1));
            currentPath = copy.Path;
            rangeStart = copy.Revision.Value;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/xml; charset=utf-8";
        await using var writer = XmlWriter.Create(response.Body, Settings());
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        writer.WriteStartElement("S", "get-location-segments-report", ProtocolNamespace);
        writer.WriteAttributeString("xmlns", "D", null, SvnDavXml.Dav);
        foreach (var segment in segments) {
            writer.WriteStartElement("S", "location-segment", ProtocolNamespace);
            if (segment.Path is not null) writer.WriteAttributeString("path", segment.Path);
            writer.WriteAttributeString("range-start", segment.End.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("range-end", segment.Start.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }
    private static async ValueTask<SvnCopySource?> FindCopySourceAsync(ISvnRepository repository, SvnRepositoryPath path,
        SvnRevision revision, CancellationToken token) {
        await foreach (var entry in repository.GetLogAsync(new([path], revision, revision), token).ConfigureAwait(false)) {
            var change = entry.ChangedPaths.FirstOrDefault(value => value.Path == path);
            if (change?.CopyFromPath is { } sourcePath && change.CopyFromRevision is { } sourceRevision)
                return new(sourcePath, sourceRevision);
        }
        return null;
    }


    internal static async Task WriteLocationsAsync(HttpRequest request, HttpResponse response, ISvnRepository repository,
        SvnRepositoryPath basePath, long maximumSize, CancellationToken token) {
        var report = await ReadLocationsAsync(request, maximumSize, token).ConfigureAwait(false);
        var pegPath = report.Path.IsRoot ? basePath : basePath.Append(report.Path);
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/xml; charset=utf-8";
        await using var writer = XmlWriter.Create(response.Body, Settings());
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        writer.WriteStartElement("S", "get-locations-report", ProtocolNamespace);
        writer.WriteAttributeString("xmlns", "D", null, SvnDavXml.Dav);
        foreach (var revision in report.Revisions) {
            var path = await TracePathAsync(repository, pegPath, report.PegRevision, revision, token).ConfigureAwait(false);
            if (path is null) continue;
            writer.WriteStartElement("S", "location", ProtocolNamespace);
            writer.WriteAttributeString("rev", revision.Value.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("path", "/" + path.Value.Value);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static async ValueTask<SvnRepositoryPath?> TracePathAsync(ISvnRepository repository, SvnRepositoryPath path,
        SvnRevision pegRevision, SvnRevision targetRevision, CancellationToken token) {
        var currentPath = path;
        var currentRevision = pegRevision.Value;
        while (currentRevision >= targetRevision.Value) {
            var boundary = currentRevision;
            while (boundary > targetRevision.Value) {
                var previous = await repository.OpenRevisionAsync(new(boundary - 1), token).ConfigureAwait(false);
                if (await previous.GetNodeInfoAsync(currentPath, token).ConfigureAwait(false) is null) break;
                boundary--;
            }
            if (boundary == targetRevision.Value) return currentPath;
            var copy = await FindCopySourceAsync(repository, currentPath, new(boundary), token).ConfigureAwait(false);
            if (copy is null || copy.Revision.Value >= currentRevision) return null;
            currentPath = copy.Path;
            currentRevision = copy.Revision.Value;
        }
        return null;
    }

    private static async ValueTask<LocationsRequest> ReadLocationsAsync(HttpRequest request, long maximumSize, CancellationToken token) {
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximumSize };
        using var reader = XmlReader.Create(request.Body, settings);
        if (await reader.MoveToContentAsync().ConfigureAwait(false) != XmlNodeType.Element || reader.LocalName != "get-locations")
            throw new BadHttpRequestException("Expected an svn:get-locations document.");
        var path = new SvnRepositoryPath("");
        SvnRevision? peg = null;
        var revisions = new List<SvnRevision>();
        await reader.ReadAsync().ConfigureAwait(false);
        while (!reader.EOF) {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 0) break;
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1 || reader.NamespaceURI != ProtocolNamespace) {
                await reader.ReadAsync().ConfigureAwait(false);
                continue;
            }
            switch (reader.LocalName) {
                case "path": path = new(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false)); continue;
                case "peg-revision": peg = Revision(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false)); continue;
                case "location-revision": revisions.Add(Revision(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false))); continue;
            }
            await reader.ReadAsync().ConfigureAwait(false);
        }
        if (peg is null) throw new BadHttpRequestException("The locations peg revision is missing.");
        return new(path, peg.Value, revisions);
    }

    internal static async Task WriteFileRevisionsAsync(HttpRequest request, HttpResponse response, ISvnRepository repository,
        SvnRepositoryPath basePath, SvnDiffVersion version, long maximumSize, CancellationToken token) {
        var report = await ReadFileRevisionsAsync(request, maximumSize, token).ConfigureAwait(false);
        var path = report.Path.IsRoot ? basePath : basePath.Append(report.Path);
        var first = Math.Min(report.Start.Value, report.End.Value);
        var last = Math.Max(report.Start.Value, report.End.Value);

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/xml; charset=utf-8";
        await using var writer = XmlWriter.Create(response.Body, Settings());
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        writer.WriteStartElement("S", "file-revs-report", ProtocolNamespace);
        writer.WriteAttributeString("xmlns", "D", null, SvnDavXml.Dav);
        ISvnRevisionRoot? previousRoot = null;
        for (var revision = first; revision <= last; revision++) {
            var root = await repository.OpenRevisionAsync(new(revision), token).ConfigureAwait(false);
            var info = await root.GetNodeInfoAsync(path, token).ConfigureAwait(false);
            if (info?.Kind != SvnNodeKind.File) continue;
            writer.WriteStartElement("S", "file-rev", ProtocolNamespace);
            writer.WriteAttributeString("path", "/" + path.Value);
            writer.WriteAttributeString("rev", revision.ToString(CultureInfo.InvariantCulture));
            WriteRevisionProperties(writer, await repository.GetRevisionPropertiesAsync(new(revision), token).ConfigureAwait(false));
            writer.WriteStartElement("S", "txdelta", ProtocolNamespace);
            await writer.WriteBase64Async(SvnDiffEncoder.EncodeHeader(version), 0, 4).ConfigureAwait(false);
            await using var target = await root.OpenFileAsync(path, token).ConfigureAwait(false);
            await using var source = previousRoot is null ? null : await previousRoot.OpenFileAsync(path, token).ConfigureAwait(false);
            var targetBuffer = new byte[64 * 1024];
            var sourceBuffer = new byte[64 * 1024];
            long sourceOffset = 0;
            while (true) {
                var targetCount = await ReadChunkAsync(target, targetBuffer, token).ConfigureAwait(false);
                if (targetCount == 0) break;
                var sourceCount = source is null ? 0 : await ReadChunkAsync(source, sourceBuffer, token).ConfigureAwait(false);
                var window = SvnDiffEncoder.EncodeWindow(sourceOffset, sourceBuffer.AsSpan(0, sourceCount), targetBuffer.AsSpan(0, targetCount), version);
                await writer.WriteBase64Async(window, 0, window.Length).ConfigureAwait(false);
                sourceOffset += sourceCount;
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
            previousRoot = root;
        }
        writer.WriteEndElement();
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static async ValueTask<FileRevisionsRequest> ReadFileRevisionsAsync(HttpRequest request, long maximumSize, CancellationToken token) {
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximumSize };
        using var reader = XmlReader.Create(request.Body, settings);
        if (await reader.MoveToContentAsync().ConfigureAwait(false) != XmlNodeType.Element || reader.LocalName != "file-revs-report")
            throw new BadHttpRequestException("Expected an svn:file-revs-report document.");
        var path = new SvnRepositoryPath("");
        SvnRevision? start = null;
        SvnRevision? end = null;
        await reader.ReadAsync().ConfigureAwait(false);
        while (!reader.EOF) {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 0) break;
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1 || reader.NamespaceURI != ProtocolNamespace) {
                await reader.ReadAsync().ConfigureAwait(false);
                continue;
            }
            switch (reader.LocalName) {
                case "path": path = new(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false)); continue;
                case "start-revision": start = Revision(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false)); continue;
                case "end-revision": end = Revision(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false)); continue;
            }
            await reader.ReadAsync().ConfigureAwait(false);
        }
        if (start is null || end is null) throw new BadHttpRequestException("The file revisions range is incomplete.");
        return new(path, start.Value, end.Value);
    }

    private static void WriteRevisionProperties(XmlWriter writer, SvnRevisionProperties properties) {
        if (properties.Author is not null) WriteValue(writer, "svn:author", properties.Author);
        if (properties.Date is { } date) WriteValue(writer, "svn:date", date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
        if (properties.LogMessage is not null) WriteValue(writer, "svn:log", properties.LogMessage);
        foreach (var property in properties.CustomProperties) {
            writer.WriteStartElement("S", "rev-prop", ProtocolNamespace);
            writer.WriteAttributeString("name", property.Name);
            var bytes = property.Value.ToArray();
            writer.WriteAttributeString("encoding", "base64");
            writer.WriteBase64(bytes, 0, bytes.Length);
            writer.WriteEndElement();
        }
    }

    private static void WriteValue(XmlWriter writer, string name, string value) {
        writer.WriteStartElement("S", "rev-prop", ProtocolNamespace);
        writer.WriteAttributeString("name", name);
        writer.WriteString(value);
        writer.WriteEndElement();
    }

    private static async ValueTask<int> ReadChunkAsync(Stream stream, Memory<byte> buffer, CancellationToken token) {
        var count = 0;
        while (count < buffer.Length) {
            var read = await stream.ReadAsync(buffer[count..], token).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        return count;
    }

    private static async ValueTask<LocationRequest> ReadLocationSegmentsAsync(HttpRequest request, SvnRevision latest, long maximumSize, CancellationToken token) {
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximumSize };
        using var reader = XmlReader.Create(request.Body, settings);
        if (await reader.MoveToContentAsync().ConfigureAwait(false) != XmlNodeType.Element || reader.LocalName != "get-location-segments")
            throw new BadHttpRequestException("Expected an svn:get-location-segments document.");
        var path = new SvnRepositoryPath("");
        var peg = latest;
        var start = latest;
        var end = new SvnRevision(0);
        await reader.ReadAsync().ConfigureAwait(false);
        while (!reader.EOF) {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 0) break;
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1 || reader.NamespaceURI != ProtocolNamespace) {
                await reader.ReadAsync().ConfigureAwait(false);
                continue;
            }
            switch (reader.LocalName) {
                case "path": path = new(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false)); continue;
                case "peg-revision": peg = Revision(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false)); continue;
                case "start-revision": start = Revision(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false)); continue;
                case "end-revision": end = Revision(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false)); continue;
            }
            await reader.ReadAsync().ConfigureAwait(false);
        }
        if (start.Value > peg.Value || end.Value > start.Value) throw new BadHttpRequestException("The location segment revision range is invalid.");
        return new(path, peg, start, end);
    }

    private static SvnRevision Revision(string value) {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0)
            throw new BadHttpRequestException("A location segment revision is invalid.");
        return new(revision);
    }

    private static XmlWriterSettings Settings() => new() { Async = true, Encoding = new UTF8Encoding(false), CloseOutput = false };
    private sealed record LocationRequest(SvnRepositoryPath Path, SvnRevision Peg, SvnRevision Start, SvnRevision End);
    private sealed record LocationsRequest(SvnRepositoryPath Path, SvnRevision PegRevision, IReadOnlyList<SvnRevision> Revisions);
    private sealed record FileRevisionsRequest(SvnRepositoryPath Path, SvnRevision Start, SvnRevision End);
    private sealed record LocationSegment(string? Path, long Start, long End);
}

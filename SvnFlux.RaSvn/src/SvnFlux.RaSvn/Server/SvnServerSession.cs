using System.Globalization;
using System.Text;
using SvnFlux.Core;
using SvnFlux.RaSvn.Protocol;
using SvnFlux.RaSvn.Wire;
using SvnFlux.Svndiff;
using static SvnFlux.RaSvn.Protocol.SvnProtocolItems;

namespace SvnFlux.RaSvn.Server;

internal sealed class SvnServerSession
{
    private const int FileChunkSize = 64 * 1024;
    private readonly SvnWireReader _reader;
    private readonly SvnWireWriter _writer;
    private readonly ISvnRepositoryResolver _repositoryResolver;
    private readonly SvnServerOptions _options;
    private SvnResolvedRepository? _resolvedRepository;
    private SvnDiffVersion _svndiffVersion = SvnDiffVersion.Zero;

    public SvnServerSession(Stream stream, ISvnRepositoryResolver repositoryResolver, SvnServerOptions options)
    {
        _reader = new SvnWireReader(stream);
        _writer = new SvnWireWriter(stream);
        _repositoryResolver = repositoryResolver;
        _options = options;
    }

    public SvnProtocolState State { get; private set; } = SvnProtocolState.Handshake;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var requestedUri = await PerformHandshakeAsync(cancellationToken).ConfigureAwait(false);
            State = SvnProtocolState.Authentication;
            await PerformAuthenticationAsync(requestedUri, cancellationToken).ConfigureAwait(false);
            State = SvnProtocolState.MainCommands;

            while (!cancellationToken.IsCancellationRequested)
            {
                SvnWireItem item;
                try
                {
                    item = await _reader.ReadItemAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    State = SvnProtocolState.Closed;
                    return;
                }

                _options.ProtocolTrace?.Invoke($"CLIENT → {DescribeWireItem(item)}");
                await DispatchAsync(RaSvnCommandDecoder.Decode(item), cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            State = SvnProtocolState.Failed;
            throw;
        }
    }

    private async ValueTask<Uri> PerformHandshakeAsync(CancellationToken cancellationToken)
    {
        _options.DiagnosticLog?.Invoke("Sending protocol greeting.");
        await _writer.WriteItemAsync(Success(Number(2), Number(2), EmptyList(),
            List(Word("edit-pipeline"), Word("svndiff1"), Word("depth"), Word("commit-revprops"), Word("atomic-revprops"))), cancellationToken).ConfigureAwait(false);

        if (await _reader.ReadItemAsync(cancellationToken).ConfigureAwait(false) is not SvnWireList response ||
            response.Items.Count < 3 || response.Items[0] is not SvnWireNumber { Value: 2 })
        {
            throw new SvnWireProtocolException("The client selected an unsupported protocol version.");
        }

        if (response.Items[1] is not SvnWireList clientCapabilities)
        {
            throw new SvnWireProtocolException("The client capability field must be a list.");
        }

        _svndiffVersion = clientCapabilities.Items.Any(item => item is SvnWireWord { Value: "svndiff1" })
            ? SvnDiffVersion.One
            : SvnDiffVersion.Zero;

        var url = GetText(response.Items[2], "url");
        _options.DiagnosticLog?.Invoke($"Client selected protocol 2 and svndiff{(byte)_svndiffVersion} for {url}.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var requestedUri) || requestedUri.Scheme != "svn")
        {
            throw new SvnWireProtocolException("The client supplied an invalid svn repository URL.");
        }

        return requestedUri;
    }

    private async ValueTask PerformAuthenticationAsync(Uri requestedUri, CancellationToken cancellationToken)
    {
        _options.DiagnosticLog?.Invoke("Sending anonymous authentication request and repository information.");
        await WriteNoAuthenticationRequestAsync(cancellationToken).ConfigureAwait(false);
        _resolvedRepository = await _repositoryResolver.ResolveAsync(requestedUri, cancellationToken).ConfigureAwait(false);
        if (_resolvedRepository is null)
        {
            await _writer.WriteItemAsync(Failure(170000, "The requested repository was not found."), cancellationToken).ConfigureAwait(false);
            throw new SvnWireProtocolException("The requested repository was not resolved.");
        }

        await _writer.WriteItemAsync(
            Success(
                Text(_resolvedRepository.Repository.Id.ToString()),
                Text(_resolvedRepository.RepositoryRootUri.AbsoluteUri.TrimEnd('/')),
                EmptyList()),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask DispatchAsync(RaSvnCommand command, CancellationToken cancellationToken)
    {
        if (State != SvnProtocolState.MainCommands || _resolvedRepository is null)
        {
            throw new SvnWireProtocolException("A main command was received outside the main-command state.");
        }

        await WriteNoAuthenticationRequestAsync(cancellationToken).ConfigureAwait(false);
        _options.DiagnosticLog?.Invoke($"Received command {command.GetType().Name}.");
        try
        {
            switch (command)
            {
                case GetLatestRevisionCommand:
                    await WriteLatestRevisionAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case CheckPathCommand checkPath:
                    await WriteCheckPathAsync(checkPath, cancellationToken).ConfigureAwait(false);
                    break;
                case StatCommand stat:
                    await WriteStatAsync(stat, cancellationToken).ConfigureAwait(false);
                    break;
                case GetDirectoryCommand getDirectory:
                    await WriteDirectoryAsync(getDirectory, cancellationToken).ConfigureAwait(false);
                    break;
                case GetFileCommand getFile:
                    await WriteFileAsync(getFile, cancellationToken).ConfigureAwait(false);
                    break;
                case GetLockCommand getLock:
                    await WriteLockAsync(getLock, cancellationToken).ConfigureAwait(false);
                    break;
                case GetLocksCommand getLocks:
                    await WriteLocksAsync(getLocks, cancellationToken).ConfigureAwait(false);
                    break;
                case LockCommand lockCommand:
                    await LockAsync(lockCommand, cancellationToken).ConfigureAwait(false);
                    break;
                case UnlockCommand unlock:
                    await UnlockAsync(unlock, cancellationToken).ConfigureAwait(false);
                    break;
                case LockManyCommand lockMany:
                    await LockManyAsync(lockMany, cancellationToken).ConfigureAwait(false);
                    break;
                case UnlockManyCommand unlockMany:
                    await UnlockManyAsync(unlockMany, cancellationToken).ConfigureAwait(false);
                    break;
                case RevisionPropertyListCommand revisionProperties:
                    await WriteRevisionPropertiesAsync(revisionProperties, cancellationToken).ConfigureAwait(false);
                    break;
                case RevisionPropertyCommand revisionProperty:
                    await WriteRevisionPropertyAsync(revisionProperty, cancellationToken).ConfigureAwait(false);
                    break;
                case ChangeRevisionPropertyCommand changeRevisionProperty:
                    await ChangeRevisionPropertyAsync(changeRevisionProperty, cancellationToken).ConfigureAwait(false);
                    break;
                case GetFileRevisionsCommand fileRevisions:
                    await WriteFileRevisionsAsync(fileRevisions, cancellationToken).ConfigureAwait(false);
                    break;
                case GetLocationsCommand locations:
                    await WriteLocationsAsync(locations, cancellationToken).ConfigureAwait(false);
                    break;
                case GetLocationSegmentsCommand segments:
                    await WriteLocationSegmentsAsync(segments, cancellationToken).ConfigureAwait(false);
                    break;
                case CommitCommand commit:
                    await WriteCommitAsync(commit, cancellationToken).ConfigureAwait(false);
                    break;
                case UpdateCommand update:
                    await WriteUpdateAsync(update, cancellationToken).ConfigureAwait(false);
                    break;
                case StatusCommand status:
                    await WriteReportDrivenEditAsync(status.Revision, status.Target, status.Depth, "status", cancellationToken).ConfigureAwait(false);
                    break;
                case DiffCommand diff:
                    await WriteReportDrivenEditAsync(diff.Revision, new SvnRepositoryPath(string.Empty), diff.Depth, "diff", cancellationToken, diff.Target).ConfigureAwait(false);
                    break;
                case SwitchCommand switchCommand:
                    await WriteSwitchAsync(switchCommand, cancellationToken).ConfigureAwait(false);
                    break;
                case LogCommand log:
                    await WriteLogAsync(log, cancellationToken).ConfigureAwait(false);
                    break;
                case ReparentCommand reparent:
                    await ReparentAsync(reparent, cancellationToken).ConfigureAwait(false);
                    break;
                case UnknownCommand unknown:
                    await _writer.WriteItemAsync(Failure(210001, $"Command '{unknown.Name}' is not implemented by this SvnFlux milestone."), cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (SvnRepositoryException exception)
        {
            await _writer.WriteItemAsync(ToFailure(exception), cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask WriteLatestRevisionAsync(CancellationToken cancellationToken)
    {
        var revision = await Repository.GetLatestRevisionAsync(cancellationToken).ConfigureAwait(false);
        await _writer.WriteItemAsync(Success(Number(revision.Value)), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteCheckPathAsync(CheckPathCommand command, CancellationToken cancellationToken)
    {
        var (_, root) = await OpenRevisionAsync(command.Revision, cancellationToken).ConfigureAwait(false);
        var info = await root.GetNodeInfoAsync(ResolvePath(command.Path), cancellationToken).ConfigureAwait(false);
        await _writer.WriteItemAsync(Success(Word(ToNodeKindWord(info?.Kind ?? SvnNodeKind.None))), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteStatAsync(StatCommand command, CancellationToken cancellationToken)
    {
        var (_, root) = await OpenRevisionAsync(command.Revision, cancellationToken).ConfigureAwait(false);
        var info = await root.GetNodeInfoAsync(ResolvePath(command.Path), cancellationToken).ConfigureAwait(false);
        var response = info is null ? Success(EmptyList()) : Success(List(ToDirectoryTuple(info)));
        await _writer.WriteItemAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteDirectoryAsync(GetDirectoryCommand command, CancellationToken cancellationToken)
    {
        var (revision, root) = await OpenRevisionAsync(command.Revision, cancellationToken).ConfigureAwait(false);
        var path = ResolvePath(command.Path);
        var info = await root.GetNodeInfoAsync(path, cancellationToken).ConfigureAwait(false)
            ?? throw new SvnPathNotFoundException(path);
        if (info.Kind != SvnNodeKind.Directory)
        {
            throw new SvnNodeKindMismatchException(path, SvnNodeKind.Directory);
        }

        var properties = command.WantProperties
            ? await root.GetPropertiesAsync(path, cancellationToken).ConfigureAwait(false)
            : SvnPropertyCollection.Empty;
        var entries = new List<SvnWireItem>();
        if (command.WantContents)
        {
            await foreach (var entry in root.GetDirectoryAsync(path, cancellationToken).ConfigureAwait(false))
            {
                entries.Add(new SvnWireList([Text(entry.Name), .. ToDirectoryTuple(entry.NodeInfo).Items]));
            }
        }

        await _writer.WriteItemAsync(
            Success(Number(revision.Value), ToPropertyList(properties), new SvnWireList(entries)),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteFileAsync(GetFileCommand command, CancellationToken cancellationToken)
    {
        var (revision, root) = await OpenRevisionAsync(command.Revision, cancellationToken).ConfigureAwait(false);
        var path = ResolvePath(command.Path);
        var info = await root.GetNodeInfoAsync(path, cancellationToken).ConfigureAwait(false)
            ?? throw new SvnPathNotFoundException(path);
        if (info.Kind != SvnNodeKind.File)
        {
            throw new SvnNodeKindMismatchException(path, SvnNodeKind.File);
        }

        var properties = command.WantProperties
            ? await root.GetPropertiesAsync(path, cancellationToken).ConfigureAwait(false)
            : SvnPropertyCollection.Empty;
        var checksum = info.ContentChecksum is { Algorithm: SvnChecksumAlgorithm.Md5 } value
            ? List(Text(value.ToHexString()))
            : EmptyList();
        await using var content = command.WantContents
            ? await root.OpenFileAsync(path, cancellationToken).ConfigureAwait(false)
            : null;

        await _writer.WriteItemAsync(
            Success(checksum, Number(revision.Value), ToPropertyList(properties)),
            cancellationToken).ConfigureAwait(false);
        if (!command.WantContents)
        {
            return;
        }

        var buffer = new byte[FileChunkSize];
        while (true)
        {
            var count = await content!.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            await _writer.WriteItemAsync(new SvnWireString(buffer.AsMemory(0, count)), cancellationToken).ConfigureAwait(false);
        }

        await _writer.WriteItemAsync(new SvnWireString(ReadOnlyMemory<byte>.Empty), cancellationToken).ConfigureAwait(false);
        await _writer.WriteItemAsync(Success(), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteRevisionPropertiesAsync(RevisionPropertyListCommand command, CancellationToken cancellationToken)
    {
        var properties = await Repository.GetRevisionPropertiesAsync(command.Revision, cancellationToken).ConfigureAwait(false);
        await _writer.WriteItemAsync(Success(ToRevisionPropertyList(properties)), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteLockAsync(GetLockCommand command, CancellationToken cancellationToken) {
        var value = Repository is ISvnWritableRepository writable ? await writable.GetLockAsync(ResolvePath(command.Path), cancellationToken).ConfigureAwait(false) : null;
        await _writer.WriteItemAsync(Success(value is null ? EmptyList() : List(ToLockDescription(value))), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteLocksAsync(GetLocksCommand command, CancellationToken cancellationToken) {
        var values = new List<SvnWireItem>();
        if (Repository is ISvnWritableRepository writable) { await foreach (var value in writable.GetLocksAsync(ResolvePath(command.Path), cancellationToken).ConfigureAwait(false)) { values.Add(ToLockDescription(value)); } }
        await _writer.WriteItemAsync(Success(new SvnWireList(values)), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask LockAsync(LockCommand command, CancellationToken cancellationToken) {
        if (Repository is not ISvnWritableRepository writable) { throw new SvnWireProtocolException("The selected repository is read-only."); }
        var value = await writable.LockAsync(new SvnLockRequest(ResolvePath(command.Path), "anonymous", command.Comment, command.StealLock, command.CurrentRevision), cancellationToken).ConfigureAwait(false);
        await _writer.WriteItemAsync(Success(ToLockDescription(value)), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask UnlockAsync(UnlockCommand command, CancellationToken cancellationToken) {
        if (Repository is not ISvnWritableRepository writable) { throw new SvnWireProtocolException("The selected repository is read-only."); }
        await writable.UnlockAsync(ResolvePath(command.Path), command.Token, command.BreakLock, cancellationToken).ConfigureAwait(false);
        await _writer.WriteItemAsync(Success(), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask LockManyAsync(LockManyCommand command, CancellationToken cancellationToken) {
        if (Repository is not ISvnWritableRepository writable) { throw new SvnWireProtocolException("The selected repository is read-only."); }
        foreach (var target in command.Targets) {
            try {
                var value = await writable.LockAsync(new SvnLockRequest(ResolvePath(target.Path), "anonymous", command.Comment, command.StealLock, target.Revision), cancellationToken).ConfigureAwait(false);
                await _writer.WriteItemAsync(List(Word("success"), ToLockDescription(value)), cancellationToken).ConfigureAwait(false);
            }
            catch (SvnRepositoryException exception) { await _writer.WriteItemAsync(ToFailure(exception), cancellationToken).ConfigureAwait(false); }
        }
        await _writer.WriteItemAsync(Word("done"), cancellationToken).ConfigureAwait(false);
        await _writer.WriteItemAsync(Success(), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask UnlockManyAsync(UnlockManyCommand command, CancellationToken cancellationToken) {
        if (Repository is not ISvnWritableRepository writable) { throw new SvnWireProtocolException("The selected repository is read-only."); }
        foreach (var target in command.Targets) {
            try {
                await writable.UnlockAsync(ResolvePath(target.Path), target.Token, command.BreakLock, cancellationToken).ConfigureAwait(false);
                await _writer.WriteItemAsync(Success(Text(target.Path.Value)), cancellationToken).ConfigureAwait(false);
            }
            catch (SvnRepositoryException exception) { await _writer.WriteItemAsync(ToFailure(exception), cancellationToken).ConfigureAwait(false); }
        }
        await _writer.WriteItemAsync(Word("done"), cancellationToken).ConfigureAwait(false);
        await _writer.WriteItemAsync(Success(), cancellationToken).ConfigureAwait(false);
    }

    private static SvnWireList ToLockDescription(SvnLock value) => List(
        Text("/" + value.Path.Value), Text(value.Token), Text(value.Owner), OptionalText(value.Comment), Text(FormatDate(value.Created)), OptionalText(value.Expires is null ? null : FormatDate(value.Expires.Value)));

    private async ValueTask WriteRevisionPropertyAsync(RevisionPropertyCommand command, CancellationToken cancellationToken) {
        var properties = await Repository.GetRevisionPropertiesAsync(command.Revision, cancellationToken).ConfigureAwait(false);
        var value = GetRevisionProperty(properties, command.Name);
        await _writer.WriteItemAsync(Success(value is null ? EmptyList() : List(new SvnWireString(value.Value))), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ChangeRevisionPropertyAsync(ChangeRevisionPropertyCommand command, CancellationToken cancellationToken) {
        if (Repository is not ISvnWritableRepository writable) { throw new SvnWireProtocolException("The selected repository is read-only."); }
        await writable.ChangeRevisionPropertyAsync(new SvnRevisionPropertyChange(command.Revision, command.Name, command.Value, command.IgnoreExpectedValue, command.ExpectedValue), cancellationToken).ConfigureAwait(false);
        await _writer.WriteItemAsync(Success(), cancellationToken).ConfigureAwait(false);
    }

    private static ReadOnlyMemory<byte>? GetRevisionProperty(SvnRevisionProperties properties, string name) => name switch {
        "svn:author" => properties.Author is null ? null : Encoding.UTF8.GetBytes(properties.Author),
        "svn:date" => properties.Date is null ? null : Encoding.UTF8.GetBytes(FormatDate(properties.Date.Value)),
        "svn:log" => properties.LogMessage is null ? null : Encoding.UTF8.GetBytes(properties.LogMessage),
        _ => properties.CustomProperties.FirstOrDefault(property => property.Name == name)?.Value
    };

    private async ValueTask WriteFileRevisionsAsync(GetFileRevisionsCommand command, CancellationToken cancellationToken) {
        var latest = await Repository.GetLatestRevisionAsync(cancellationToken).ConfigureAwait(false);
        var start = command.StartRevision ?? new SvnRevision(0);
        var end = command.EndRevision ?? latest;
        if (start.Value > end.Value) { throw new SvnWireProtocolException("Reverse get-file-revs requires the get-file-revs-reversed capability, which is not advertised."); }
        var history = await BuildFileHistoryAsync(ResolvePath(command.Path), start, end, cancellationToken).ConfigureAwait(false);
        FileHistoryPoint? previousPoint = null;
        var previousProperties = SvnPropertyCollection.Empty;
        foreach (var point in history) {
            var root = await Repository.OpenRevisionAsync(point.Revision, cancellationToken).ConfigureAwait(false);
            var properties = await root.GetPropertiesAsync(point.Path, cancellationToken).ConfigureAwait(false);
            var revisionProperties = await Repository.GetRevisionPropertiesAsync(point.Revision, cancellationToken).ConfigureAwait(false);
            await _writer.WriteItemAsync(List(
                Text("/" + point.Path.Value),
                Number(point.Revision.Value),
                ToRevisionPropertyList(revisionProperties),
                ToPropertyDelta(previousProperties, properties),
                Word("false")), cancellationToken).ConfigureAwait(false);

            await WriteFileRevisionDeltaAsync(point, previousPoint, cancellationToken).ConfigureAwait(false);
            await _writer.WriteItemAsync(new SvnWireString(ReadOnlyMemory<byte>.Empty), cancellationToken).ConfigureAwait(false);
            previousPoint = point;
            previousProperties = properties;
        }

        await _writer.WriteItemAsync(Word("done"), cancellationToken).ConfigureAwait(false);
        await _writer.WriteItemAsync(Success(), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteLocationsAsync(GetLocationsCommand command, CancellationToken cancellationToken) {
        var pegPath = ResolvePath(command.Path);
        foreach (var revision in command.Revisions) {
            var location = await FindLocationAsync(pegPath, command.PegRevision, revision, cancellationToken).ConfigureAwait(false);
            if (location is not null) { await _writer.WriteItemAsync(List(Number(revision.Value), Text("/" + location.Value.Value)), cancellationToken).ConfigureAwait(false); }
        }
        await _writer.WriteItemAsync(Word("done"), cancellationToken).ConfigureAwait(false);
        await _writer.WriteItemAsync(Success(), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteLocationSegmentsAsync(GetLocationSegmentsCommand command, CancellationToken cancellationToken) {
        var latest = await Repository.GetLatestRevisionAsync(cancellationToken).ConfigureAwait(false);
        var peg = command.PegRevision ?? latest;
        var start = command.StartRevision ?? peg;
        var end = command.EndRevision ?? new SvnRevision(0);
        if (start.Value < end.Value || start.Value > peg.Value) { throw new SvnWireProtocolException("Location segment revision range is invalid."); }
        var pegPath = ResolvePath(command.Path);
        var values = new List<(long Revision, SvnRepositoryPath? Path)>();
        for (var value = start.Value; value >= end.Value; value--) {
            values.Add((value, await FindLocationAsync(pegPath, peg, new SvnRevision(value), cancellationToken).ConfigureAwait(false)));
        }
        for (var index = 0; index < values.Count;) {
            var high = values[index].Revision;
            var location = values[index].Path;
            var low = high;
            index++;
            while (index < values.Count && values[index].Revision == low - 1 && values[index].Path == location) { low = values[index++].Revision; }
            await _writer.WriteItemAsync(List(Number(low), Number(high), location is null ? EmptyList() : List(Text("/" + location.Value.Value))), cancellationToken).ConfigureAwait(false);
        }
        await _writer.WriteItemAsync(Word("done"), cancellationToken).ConfigureAwait(false);
        await _writer.WriteItemAsync(Success(), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SvnRepositoryPath?> FindLocationAsync(SvnRepositoryPath pegPath, SvnRevision pegRevision, SvnRevision targetRevision, CancellationToken cancellationToken) {
        if (targetRevision.Value > pegRevision.Value) { return null; }
        var path = pegPath;
        var revisionValue = pegRevision.Value;
        while (revisionValue > targetRevision.Value) {
            var revision = new SvnRevision(revisionValue);
            SvnChangedPath? exactChange = null;
            await foreach (var entry in Repository.GetLogAsync(new SvnLogQuery([path], revision, revision), cancellationToken).ConfigureAwait(false)) { exactChange = entry.ChangedPaths.FirstOrDefault(change => change.Path == path); }
            if (exactChange?.CopyFromPath is { } copyPath && exactChange.CopyFromRevision is { } copyRevision) {
                path = copyPath;
                revisionValue = copyRevision.Value;
                continue;
            }
            if (exactChange?.Action == SvnChangeAction.Add) { return null; }
            revisionValue--;
        }
        if (revisionValue < targetRevision.Value) { return null; }
        var root = await Repository.OpenRevisionAsync(targetRevision, cancellationToken).ConfigureAwait(false);
        return await root.GetNodeInfoAsync(path, cancellationToken).ConfigureAwait(false) is null ? null : path;
    }

    private async ValueTask<IReadOnlyList<FileHistoryPoint>> BuildFileHistoryAsync(SvnRepositoryPath endPath, SvnRevision start, SvnRevision end, CancellationToken cancellationToken) {
        var reversed = new List<FileHistoryPoint>();
        var path = endPath;
        var revisionValue = end.Value;
        while (revisionValue >= start.Value) {
            var revision = new SvnRevision(revisionValue);
            var entries = Repository.GetLogAsync(new SvnLogQuery([path], revision, revision, 0), cancellationToken);
            SvnChangedPath? exactChange = null;
            await foreach (var entry in entries.ConfigureAwait(false)) { exactChange = entry.ChangedPaths.FirstOrDefault(change => change.Path == path); }
            if (exactChange is not null && exactChange.Action != SvnChangeAction.Delete) {
                reversed.Add(new FileHistoryPoint(path, revision));
                if (exactChange.CopyFromPath is { } copyPath && exactChange.CopyFromRevision is { } copyRevision) {
                    path = copyPath;
                    revisionValue = copyRevision.Value;
                    continue;
                }
            }
            revisionValue--;
        }
        reversed.Reverse();
        return reversed;
    }

    private async ValueTask WriteFileRevisionDeltaAsync(FileHistoryPoint point, FileHistoryPoint? previousPoint, CancellationToken cancellationToken) {
        var root = await Repository.OpenRevisionAsync(point.Revision, cancellationToken).ConfigureAwait(false);
        await using var target = await root.OpenFileAsync(point.Path, cancellationToken).ConfigureAwait(false);
        Stream? source = null;
        if (previousPoint is { } previous) {
            var previousRoot = await Repository.OpenRevisionAsync(previous.Revision, cancellationToken).ConfigureAwait(false);
            source = await previousRoot.OpenFileAsync(previous.Path, cancellationToken).ConfigureAwait(false);
        }
        await using (source) {
            var header = SvnDiffEncoder.EncodeHeader(_svndiffVersion);
            await _writer.WriteItemAsync(new SvnWireString(header), cancellationToken).ConfigureAwait(false);
            var targetBuffer = new byte[FileChunkSize];
            var sourceBuffer = new byte[FileChunkSize];
            long sourceOffset = 0;
            long wireLength = header.Length;
            var windows = 0;
            while (true) {
                var targetCount = await ReadChunkAsync(target, targetBuffer, cancellationToken).ConfigureAwait(false);
                if (targetCount == 0) { break; }
                var sourceCount = source is null ? 0 : await ReadChunkAsync(source, sourceBuffer, cancellationToken).ConfigureAwait(false);
                var window = SvnDiffEncoder.EncodeWindow(sourceOffset, sourceBuffer.AsSpan(0, sourceCount), targetBuffer.AsSpan(0, targetCount), _svndiffVersion);
                await _writer.WriteItemAsync(new SvnWireString(window), cancellationToken).ConfigureAwait(false);
                sourceOffset += sourceCount;
                wireLength += window.Length;
                windows++;
            }
            _options.ProtocolTrace?.Invoke($"SERVER → file-rev /{point.Path.Value}@{point.Revision.Value}, svndiff{(byte)_svndiffVersion}={wireLength} bytes, windows={windows}");
        }
    }

    private static async ValueTask<int> ReadChunkAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken) {
        var count = 0;
        while (count < buffer.Length) {
            var read = await stream.ReadAsync(buffer[count..], cancellationToken).ConfigureAwait(false);
            if (read == 0) { break; }
            count += read;
        }
        return count;
    }

    private static SvnWireList ToPropertyDelta(SvnPropertyCollection previous, SvnPropertyCollection current) {
        var oldMap = previous.ToDictionary(property => property.Name, StringComparer.Ordinal);
        var newMap = current.ToDictionary(property => property.Name, StringComparer.Ordinal);
        var result = new List<SvnWireItem>();
        foreach (var name in oldMap.Keys.Union(newMap.Keys, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal)) {
            if (newMap.TryGetValue(name, out var value)) {
                if (!oldMap.TryGetValue(name, out var oldValue) || !oldValue.Value.Span.SequenceEqual(value.Value.Span)) { result.Add(List(Text(name), List(new SvnWireString(value.Value)))); }
            }
            else { result.Add(List(Text(name), EmptyList())); }
        }
        return new SvnWireList(result);
    }

    private void TraceFileRevisionDelta(FileHistoryPoint point, ReadOnlySpan<byte> delta) {
        if (_options.ProtocolTrace is not { } trace) { return; }
        var inspection = SvnDiffInspector.Inspect(delta);
        trace($"SERVER → file-rev /{point.Path.Value}@{point.Revision.Value}, svndiff{(byte)inspection.Version}={delta.Length} bytes, windows={inspection.Windows.Count}");
        for (var index = 0; index < inspection.Windows.Count; index++) {
            var window = inspection.Windows[index];
            trace($"  window {index}: source={window.SourceOffset}+{window.SourceLength}, target={window.TargetLength}, new-data={window.NewDataLength}");
            foreach (var instruction in window.Instructions) { trace($"    {instruction.Kind}: offset={instruction.Offset}, length={instruction.Length}"); }
        }
    }

    private async ValueTask WriteCommitAsync(CommitCommand command, CancellationToken cancellationToken) {
        if (Repository is not ISvnWritableRepository writableRepository) {
            await _writer.WriteItemAsync(Failure(210001, "The selected repository is read-only."), cancellationToken).ConfigureAwait(false);
            return;
        }

        await _writer.WriteItemAsync(Success(), cancellationToken).ConfigureAwait(false);
        State = SvnProtocolState.EditorInput;
        var revisionProperties = new SvnRevisionProperties("anonymous", DateTimeOffset.UtcNow, command.LogMessage, SvnPropertyCollection.Empty);
        var baseRevision = await Repository.GetLatestRevisionAsync(cancellationToken).ConfigureAwait(false);
        using var editor = new SvnCommitEditor(writableRepository, _resolvedRepository!.SessionPath, _resolvedRepository.RepositoryRootUri, baseRevision, command.LockTokens, command.KeepLocks, _options.ProtocolTrace);
        while (true) {
            var item = await _reader.ReadItemAsync(cancellationToken).ConfigureAwait(false);
            _options.ProtocolTrace?.Invoke($"CLIENT editor-wire → {DescribeWireItem(item)}");
            var editorCommand = RaSvnEditorCommandDecoder.Decode(item);
            CommitEditorResult result;
            try { result = await editor.ProcessAsync(editorCommand, revisionProperties, cancellationToken).ConfigureAwait(false); }
            catch (SvnRepositoryException exception) {
                await _writer.WriteItemAsync(ToFailure(exception), cancellationToken).ConfigureAwait(false);
                await DrainFailedEditAsync(cancellationToken).ConfigureAwait(false);
                State = SvnProtocolState.MainCommands;
                return;
            }
            catch (SvnWireProtocolException exception) {
                await _writer.WriteItemAsync(Failure(210004, exception.Message), cancellationToken).ConfigureAwait(false);
                await DrainFailedEditAsync(cancellationToken).ConfigureAwait(false);
                State = SvnProtocolState.MainCommands;
                return;
            }

            if (!result.IsComplete) { continue; }
            await _writer.WriteItemAsync(Success(), cancellationToken).ConfigureAwait(false);
            if (!result.IsAborted) {
                var revision = result.Revision ?? throw new InvalidOperationException("A completed commit has no revision.");
                var properties = await Repository.GetRevisionPropertiesAsync(revision, cancellationToken).ConfigureAwait(false);
                await WriteNoAuthenticationRequestAsync(cancellationToken).ConfigureAwait(false);
                await _writer.WriteItemAsync(List(Number(revision.Value), OptionalText(properties.Date is null ? null : FormatDate(properties.Date.Value)), OptionalText(properties.Author), EmptyList()), cancellationToken).ConfigureAwait(false);
                _options.ProtocolTrace?.Invoke($"SERVER → commit-info r{revision.Value}");
            }

            State = SvnProtocolState.MainCommands;
            return;
        }
    }

    private async ValueTask DrainFailedEditAsync(CancellationToken cancellationToken) {
        while (true) {
            var item = await _reader.ReadItemAsync(cancellationToken).ConfigureAwait(false);
            _options.ProtocolTrace?.Invoke($"CLIENT discarded-editor → {DescribeWireItem(item)}");
            var command = RaSvnEditorCommandDecoder.Decode(item);
            if (command is AbortEditEditorCommand or CloseEditEditorCommand) { return; }
        }
    }

    private async ValueTask WriteUpdateAsync(UpdateCommand command, CancellationToken cancellationToken)
        => await WriteReportDrivenEditAsync(command.Revision, command.Target, command.Depth, "update", cancellationToken).ConfigureAwait(false);

    private async ValueTask WriteReportDrivenEditAsync(SvnRevision? requestedRevision, SvnRepositoryPath target, string depth, string operation, CancellationToken cancellationToken, SvnRepositoryPath? scope = null, SvnRepositoryPath? targetAnchorOverride = null)
    {
        if (depth is not "infinity" and not "unknown")
        {
            throw new SvnWireProtocolException($"{operation} depth '{depth}' is not supported by this milestone.");
        }

        State = SvnProtocolState.ReportInput;
        SetPathReportCommand? rootReport = null;
        var pathReports = new List<SetPathReportCommand>();
        var deletedPaths = new List<SvnRepositoryPath>();
        while (true)
        {
            var reportCommand = RaSvnReportCommandDecoder.Decode(
                await _reader.ReadItemAsync(cancellationToken).ConfigureAwait(false));
            switch (reportCommand)
            {
                case SetPathReportCommand setPath when setPath.Path.IsRoot:
                    rootReport = setPath;
                    _options.DiagnosticLog?.Invoke($"Update report root is revision {setPath.Revision.Value}, start-empty={setPath.StartEmpty}, depth={setPath.Depth}.");
                    break;
                case SetPathReportCommand setPath:
                    pathReports.Add(setPath);
                    _options.ProtocolTrace?.Invoke($"CLIENT → set-path {setPath.Path} r{setPath.Revision.Value} start-empty={setPath.StartEmpty}");
                    break;
                case DeletePathReportCommand deletePath:
                    deletedPaths.Add(deletePath.Path);
                    _options.ProtocolTrace?.Invoke($"CLIENT → delete-path {deletePath.Path}");
                    break;
                case UnsupportedReportCommand unsupported:
                    throw new SvnWireProtocolException($"Report command '{unsupported.Name}' is not supported yet.");
                case AbortReportCommand:
                    State = SvnProtocolState.MainCommands;
                    await _writer.WriteItemAsync(Failure(200015, "The update report was aborted."), cancellationToken).ConfigureAwait(false);
                    return;
                case FinishReportCommand:
                    goto ReportComplete;
            }
        }

    ReportComplete:
        rootReport ??= new SetPathReportCommand(new SvnRepositoryPath(string.Empty), new SvnRevision(0), true, "infinity");
        var targetRevision = requestedRevision ?? await Repository.GetLatestRevisionAsync(cancellationToken).ConfigureAwait(false);
        var baseRoot = await Repository.OpenRevisionAsync(rootReport.Revision, cancellationToken).ConfigureAwait(false);
        var targetRoot = await Repository.OpenRevisionAsync(targetRevision, cancellationToken).ConfigureAwait(false);
        var anchorPath = ResolvePath(target);
        var targetAnchorPath = targetAnchorOverride ?? anchorPath;

        await WriteNoAuthenticationRequestAsync(cancellationToken).ConfigureAwait(false);
        State = SvnProtocolState.EditorOutput;
        var driver = new SvnUpdateEditorDriver(
            _writer,
            Repository,
            baseRoot,
            targetRoot,
            anchorPath,
            targetAnchorPath,
            Repository.Id,
            _svndiffVersion,
            _options.ProtocolTrace,
            CheckForEarlyEditorErrorAsync);
        await driver.DriveAsync(rootReport.StartEmpty, pathReports, deletedPaths, cancellationToken, scope).ConfigureAwait(false);

        var editResponse = await _reader.ReadItemAsync(cancellationToken).ConfigureAwait(false);
        if (editResponse is not SvnWireList { Items.Count: >= 2 } response ||
            response.Items[0] is not SvnWireWord { Value: "success" })
        {
            _options.DiagnosticLog?.Invoke($"Client rejected update editor: {editResponse}");
            throw new SvnWireProtocolException("The client rejected or malformed the update editor response.");
        }

        await _writer.WriteItemAsync(Success(), cancellationToken).ConfigureAwait(false);
        State = SvnProtocolState.MainCommands;
    }

    private async ValueTask WriteSwitchAsync(SwitchCommand command, CancellationToken cancellationToken) {
        var targetPath = ResolveRepositoryUrlPath(command.Url);
        await WriteReportDrivenEditAsync(command.Revision, command.Target, command.Depth, "switch", cancellationToken, targetAnchorOverride: targetPath).ConfigureAwait(false);
    }

    private SvnRepositoryPath ResolveRepositoryUrlPath(Uri url) {
        var root = new Uri(_resolvedRepository!.RepositoryRootUri.AbsoluteUri.TrimEnd('/') + "/");
        if (!root.IsBaseOf(url)) { throw new SvnWireProtocolException($"URL '{url}' is outside this repository."); }
        return new SvnRepositoryPath(Uri.UnescapeDataString(root.MakeRelativeUri(url).ToString()));
    }

    private async ValueTask CheckForEarlyEditorErrorAsync(CancellationToken cancellationToken)
    {
        if (!_reader.HasDataAvailable)
        {
            return;
        }

        var response = await _reader.ReadItemAsync(cancellationToken).ConfigureAwait(false);
        _options.DiagnosticLog?.Invoke($"Client returned an early editor response: {DescribeWireItem(response)}");
        throw new SvnWireProtocolException("The client rejected an update editor command.");
    }

    private static string DescribeWireItem(SvnWireItem item) => item switch
    {
        SvnWireWord word => word.Value,
        SvnWireNumber number => number.Value.ToString(CultureInfo.InvariantCulture),
        SvnWireString text => Encoding.UTF8.GetString(text.Value.Span),
        SvnWireList list => "(" + string.Join(' ', list.Items.Select(DescribeWireItem)) + ")",
        _ => item.ToString() ?? item.GetType().Name
    };

    private async ValueTask WriteLogAsync(LogCommand command, CancellationToken cancellationToken)
    {
        var latest = await Repository.GetLatestRevisionAsync(cancellationToken).ConfigureAwait(false);
        var start = command.StartRevision ?? latest;
        var end = command.EndRevision ?? new SvnRevision(0);
        var paths = command.Paths.Select(ResolvePath).ToArray();
        var query = new SvnLogQuery(paths, start, end, command.Limit);

        await foreach (var entry in Repository.GetLogAsync(query, cancellationToken).ConfigureAwait(false))
        {
            await _writer.WriteItemAsync(ToLogEntry(entry, command.IncludeChangedPaths), cancellationToken).ConfigureAwait(false);
        }

        await _writer.WriteItemAsync(Word("done"), cancellationToken).ConfigureAwait(false);
        await _writer.WriteItemAsync(Success(), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ReparentAsync(ReparentCommand command, CancellationToken cancellationToken)
    {
        var resolved = await _repositoryResolver.ResolveAsync(command.Uri, cancellationToken).ConfigureAwait(false);
        if (resolved is null || resolved.Repository.Id != Repository.Id)
        {
            await _writer.WriteItemAsync(Failure(170000, "The reparent URL does not identify the current repository."), cancellationToken).ConfigureAwait(false);
            return;
        }

        _resolvedRepository = resolved;
        await _writer.WriteItemAsync(Success(), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<(SvnRevision Revision, ISvnRevisionRoot Root)> OpenRevisionAsync(
        SvnRevision? requestedRevision,
        CancellationToken cancellationToken)
    {
        var revision = requestedRevision ?? await Repository.GetLatestRevisionAsync(cancellationToken).ConfigureAwait(false);
        var root = await Repository.OpenRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        return (revision, root);
    }

    private ISvnRepository Repository => _resolvedRepository!.Repository;
    private SvnRepositoryPath ResolvePath(SvnRepositoryPath path) => _resolvedRepository!.SessionPath.Append(path);

    private ValueTask WriteNoAuthenticationRequestAsync(CancellationToken cancellationToken) =>
        _writer.WriteItemAsync(Success(EmptyList(), Text(string.Empty)), cancellationToken);

    private static SvnWireList ToDirectoryTuple(SvnNodeInfo info) =>
        List(
            Word(ToNodeKindWord(info.Kind)),
            Number(info.Size),
            Word(info.HasProperties ? "true" : "false"),
            Number(info.LastChangedRevision.Value),
            OptionalText(info.LastChangedTime is null ? null : FormatDate(info.LastChangedTime.Value)),
            OptionalText(info.LastChangedAuthor));

    private static SvnWireList ToPropertyList(SvnPropertyCollection properties) =>
        new(properties.Select(property => (SvnWireItem)List(Text(property.Name), new SvnWireString(property.Value))).ToArray());

    private static SvnWireList ToRevisionPropertyList(SvnRevisionProperties properties)
    {
        var items = new List<SvnWireItem>();
        if (properties.Author is not null)
        {
            items.Add(List(Text("svn:author"), Text(properties.Author)));
        }

        if (properties.Date is not null)
        {
            items.Add(List(Text("svn:date"), Text(FormatDate(properties.Date.Value))));
        }

        if (properties.LogMessage is not null)
        {
            items.Add(List(Text("svn:log"), Text(properties.LogMessage)));
        }

        items.AddRange(properties.CustomProperties.Select(property =>
            (SvnWireItem)List(Text(property.Name), new SvnWireString(property.Value))));
        return new SvnWireList(items);
    }

    private static SvnWireList ToLogEntry(SvnLogEntry entry, bool includeChangedPaths)
    {
        var changes = includeChangedPaths
            ? new SvnWireList(entry.ChangedPaths.Select(ToChangedPath).Cast<SvnWireItem>().ToArray())
            : EmptyList();
        var properties = entry.RevisionProperties;
        return List(
            changes,
            Number(entry.Revision.Value),
            OptionalText(properties.Author),
            OptionalText(properties.Date is null ? null : FormatDate(properties.Date.Value)),
            OptionalText(properties.LogMessage),
            Word("false"),
            Word("false"),
            Number(0),
            EmptyList(),
            Word("false"));
    }

    private static SvnWireList ToChangedPath(SvnChangedPath change) =>
        List(
            Text("/" + change.Path.Value),
            Word(change.Action switch
            {
                SvnChangeAction.Add => "A",
                SvnChangeAction.Delete => "D",
                SvnChangeAction.Replace => "R",
                SvnChangeAction.Modify => "M",
                _ => throw new ArgumentOutOfRangeException(nameof(change))
            }),
            change.CopyFromPath is null || change.CopyFromRevision is null
                ? EmptyList()
                : List(Text("/" + change.CopyFromPath.Value.Value), Number(change.CopyFromRevision.Value.Value)),
            List(
                Text(ToNodeKindWord(change.NodeKind)),
                Word(change.TextModified ? "true" : "false"),
                Word(change.PropertiesModified ? "true" : "false")));

    private static SvnWireList OptionalText(string? value) => value is null ? EmptyList() : List(Text(value));
    private static string FormatDate(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    private static string ToNodeKindWord(SvnNodeKind kind) => kind switch
    {
        SvnNodeKind.None => "none",
        SvnNodeKind.File => "file",
        SvnNodeKind.Directory => "dir",
        _ => "unknown"
    };

    private static SvnWireList ToFailure(SvnRepositoryException exception) => exception switch
    {
        SvnPathNotFoundException => Failure(160013, exception.Message),
        SvnInvalidRevisionException => Failure(160006, exception.Message),
        SvnNodeKindMismatchException => Failure(160017, exception.Message),
        SvnOutOfDateException => Failure(160028, exception.Message),
        _ => Failure(170000, exception.Message)
    };

    private static SvnWireList Failure(long code, string message) =>
        List(Word("failure"), List(List(Number(code), Text(message), Text("SvnFlux"), Number(0))));

    private sealed record FileHistoryPoint(SvnRepositoryPath Path, SvnRevision Revision);
}

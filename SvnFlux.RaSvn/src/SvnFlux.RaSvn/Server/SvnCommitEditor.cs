using System.Security.Cryptography;
using SvnFlux.Core;
using SvnFlux.RaSvn.Protocol;
using SvnFlux.RaSvn.Wire;
using SvnFlux.Svndiff;

namespace SvnFlux.RaSvn.Server;

internal sealed class SvnCommitEditor : IDisposable {
    private const int MaximumTracedDeltaSize = 4 * 1024 * 1024;
    private readonly ISvnWritableRepository _repository;
    private readonly SvnRepositoryPath _anchorPath;
    private readonly Dictionary<string, DirectoryState> _directories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileState> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SvnCommitChange> _changes = new(StringComparer.Ordinal);
    private readonly Action<string>? _trace;
    private readonly Uri _repositoryRootUri;
    private readonly IReadOnlyDictionary<SvnRepositoryPath, string> _lockTokens;
    private readonly bool _keepLocks;
    private readonly string _temporaryPath = Path.Combine(Path.GetTempPath(), "SvnFlux.RaSvn", "commits", Guid.NewGuid().ToString("N"));
    private SvnRevision? _baseRevision;
    private ISvnRevisionRoot? _baseRoot;

    public SvnCommitEditor(ISvnWritableRepository repository, SvnRepositoryPath anchorPath, Uri repositoryRootUri, SvnRevision baseRevision, IReadOnlyDictionary<SvnRepositoryPath, string> lockTokens, bool keepLocks, Action<string>? trace) {
        _repository = repository;
        _anchorPath = anchorPath;
        _repositoryRootUri = new Uri(repositoryRootUri.AbsoluteUri.TrimEnd('/') + "/");
        _lockTokens = lockTokens.ToDictionary(pair => anchorPath.Append(pair.Key), pair => pair.Value);
        _keepLocks = keepLocks;
        _baseRevision = baseRevision;
        _trace = trace;
    }

    public async ValueTask<CommitEditorResult> ProcessAsync(RaSvnEditorCommand command, SvnRevisionProperties revisionProperties, CancellationToken cancellationToken) {
        _trace?.Invoke($"CLIENT → {Describe(command)}");
        switch (command) {
            case OpenRootEditorCommand openRoot: await OpenRootAsync(openRoot, cancellationToken).ConfigureAwait(false); break;
            case DeleteEntryEditorCommand deleteEntry: await DeleteEntryAsync(deleteEntry, cancellationToken).ConfigureAwait(false); break;
            case AddDirectoryEditorCommand addDirectory: AddDirectory(addDirectory); break;
            case OpenDirectoryEditorCommand openDirectory: await OpenDirectoryAsync(openDirectory, cancellationToken).ConfigureAwait(false); break;
            case ChangeDirectoryPropertyEditorCommand property: ChangeDirectoryProperty(property); break;
            case CloseDirectoryEditorCommand closeDirectory: CloseDirectory(closeDirectory); break;
            case AddFileEditorCommand addFile: AddFile(addFile); break;
            case OpenFileEditorCommand openFile: await OpenFileAsync(openFile, cancellationToken).ConfigureAwait(false); break;
            case ChangeFilePropertyEditorCommand property: ChangeFileProperty(property); break;
            case ApplyTextDeltaEditorCommand applyTextDelta: ApplyTextDelta(applyTextDelta); break;
            case TextDeltaChunkEditorCommand chunk: await AppendTextDeltaAsync(chunk, cancellationToken).ConfigureAwait(false); break;
            case TextDeltaEndEditorCommand end: await EndTextDeltaAsync(end, cancellationToken).ConfigureAwait(false); break;
            case CloseFileEditorCommand closeFile: CloseFile(closeFile); break;
            case AbortEditEditorCommand: return CommitEditorResult.Aborted;
            case CloseEditEditorCommand:
                EnsureEditComplete();
                var request = new SvnCommitRequest(_baseRevision ?? throw ProtocolError("The editor did not open a root."), revisionProperties, _changes.Values) { LockTokens = _lockTokens, KeepLocks = _keepLocks };
                var revision = await _repository.CommitAsync(request, cancellationToken).ConfigureAwait(false);
                return new CommitEditorResult(true, false, revision);
            default: throw ProtocolError($"Unsupported editor command {command.GetType().Name}.");
        }

        return CommitEditorResult.Continue;
    }

    private async ValueTask OpenRootAsync(OpenRootEditorCommand command, CancellationToken cancellationToken) {
        if (_baseRoot is not null) { throw ProtocolError("The commit editor root was opened twice."); }
        var baseRevision = _baseRevision ?? throw ProtocolError("The commit base revision is missing.");
        if (command.Revision is { } reported && reported != baseRevision) { throw new SvnOutOfDateException(reported, baseRevision); }
        _baseRoot = await _repository.OpenRevisionAsync(baseRevision, cancellationToken).ConfigureAwait(false);
        _directories.Add(command.Token, new DirectoryState(_anchorPath, true));
    }

    private async ValueTask DeleteEntryAsync(DeleteEntryEditorCommand command, CancellationToken cancellationToken) {
        RequireDirectory(command.ParentToken);
        var path = ResolvePath(command.Path);
        var info = await BaseRoot.GetNodeInfoAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new SvnPathNotFoundException(path);
        ValidateNodeRevision(command.Revision, info);
        RemoveChangesAtOrBelow(path);
        _changes[path.Value] = SvnCommitChange.Delete(path, info.Kind);
    }

    private void AddDirectory(AddDirectoryEditorCommand command) {
        RequireDirectory(command.ParentToken);
        var path = ResolvePath(command.Path);
        _changes[path.Value] = command.CopyFromPath is { } copyPath && command.CopyFromRevision is { } copyRevision
            ? SvnCommitChange.Copy(path, SvnNodeKind.Directory, new SvnCopySource(ResolveCopyPath(copyPath), copyRevision))
            : SvnCommitChange.AddDirectory(path);
        _directories.Add(command.Token, new DirectoryState(path, false));
    }

    private async ValueTask OpenDirectoryAsync(OpenDirectoryEditorCommand command, CancellationToken cancellationToken) {
        RequireDirectory(command.ParentToken);
        var path = ResolvePath(command.Path);
        var info = await BaseRoot.GetNodeInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (info?.Kind != SvnNodeKind.Directory) { throw new SvnNodeKindMismatchException(path, SvnNodeKind.Directory); }
        ValidateNodeRevision(command.Revision, info);
        _directories.Add(command.Token, new DirectoryState(path, false));
    }

    private void CloseDirectory(CloseDirectoryEditorCommand command) {
        if (!_directories.Remove(command.Token, out var directory)) { throw ProtocolError($"Unknown directory token '{command.Token}'."); }
        MergePropertyChanges(directory.Path, SvnNodeKind.Directory, directory.PropertyChanges);
    }

    private void ChangeDirectoryProperty(ChangeDirectoryPropertyEditorCommand command) {
        var directory = RequireDirectory(command.Token);
        directory.PropertyChanges.Add(ToPropertyChange(command.Name, command.Value));
    }

    private void AddFile(AddFileEditorCommand command) {
        RequireDirectory(command.ParentToken);
        var path = ResolvePath(command.Path);
        var copyFrom = command.CopyFromPath is { } copyPath && command.CopyFromRevision is { } copyRevision
            ? new SvnCopySource(ResolveCopyPath(copyPath), copyRevision)
            : null;
        _files.Add(command.Token, new FileState(path, true, copyFrom));
    }

    private async ValueTask OpenFileAsync(OpenFileEditorCommand command, CancellationToken cancellationToken) {
        RequireDirectory(command.ParentToken);
        var path = ResolvePath(command.Path);
        var info = await BaseRoot.GetNodeInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (info?.Kind != SvnNodeKind.File) { throw new SvnNodeKindMismatchException(path, SvnNodeKind.File); }
        ValidateNodeRevision(command.Revision, info);
        _files.Add(command.Token, new FileState(path, false));
    }

    private void ApplyTextDelta(ApplyTextDeltaEditorCommand command) {
        var file = RequireFile(command.Token);
        if (file.DeltaStarted) { throw ProtocolError("apply-textdelta was sent twice for one file."); }
        file.DeltaStarted = true;
        file.BaseChecksum = command.BaseChecksum;
    }

    private async ValueTask AppendTextDeltaAsync(TextDeltaChunkEditorCommand command, CancellationToken cancellationToken) {
        var file = RequireFile(command.Token);
        if (!file.DeltaStarted || file.DeltaEnded) { throw ProtocolError("textdelta-chunk was sent outside an active text delta."); }
        await file.GetDeltaStream(_temporaryPath).WriteAsync(command.Data, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EndTextDeltaAsync(TextDeltaEndEditorCommand command, CancellationToken cancellationToken) {
        var file = RequireFile(command.Token);
        if (!file.DeltaStarted || file.DeltaEnded) { throw ProtocolError("textdelta-end was sent outside an active text delta."); }
        await using var source = file.CopyFrom is { } copyFrom
            ? await OpenFileAsync(copyFrom.Revision, copyFrom.Path, cancellationToken).ConfigureAwait(false)
            : file.IsAdd ? new MemoryStream() : await BaseRoot.OpenFileAsync(file.Path, cancellationToken).ConfigureAwait(false);
        await VerifyChecksumAsync(source, file.BaseChecksum, "base", cancellationToken).ConfigureAwait(false);
        var delta = file.GetDeltaStream(_temporaryPath);
        await delta.FlushAsync(cancellationToken).ConfigureAwait(false);
        delta.Position = 0;
        var contentPath = Path.Combine(_temporaryPath, Guid.NewGuid().ToString("N") + ".content");
        await using var content = new FileStream(contentPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try { await SvnDiffDecoder.ApplyAsync(source, delta, content, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException) { throw new SvnWireProtocolException($"Invalid commit svndiff: {exception.Message}"); }
        file.ContentPath = contentPath;
        file.TargetChecksum = await CalculateChecksumAsync(content, cancellationToken).ConfigureAwait(false);
        file.DeltaEnded = true;
        TraceDelta(delta, source.Length, content.Length);
        file.Dispose();
    }

    private void CloseFile(CloseFileEditorCommand command) {
        var file = RequireFile(command.Token);
        if (file.DeltaStarted && !file.DeltaEnded) { throw ProtocolError("The file was closed before textdelta-end."); }
        if (file.ContentPath is null && file.CopyFrom is { } copyFrom) { _changes[file.Path.Value] = SvnCommitChange.Copy(file.Path, SvnNodeKind.File, copyFrom); }
        if (file.ContentPath is null && file.IsAdd && file.CopyFrom is null) {
            Directory.CreateDirectory(_temporaryPath);
            file.ContentPath = Path.Combine(_temporaryPath, Guid.NewGuid().ToString("N") + ".content");
            File.WriteAllBytes(file.ContentPath, []);
            file.TargetChecksum = Convert.ToHexStringLower(MD5.HashData([]));
        }
        if (file.ContentPath is not null) {
            VerifyChecksum(file.TargetChecksum!, command.TextChecksum, "target");
            var contentPath = file.ContentPath;
            _changes[file.Path.Value] = file.IsAdd
                ? SvnCommitChange.AddFileStream(file.Path, _ => ValueTask.FromResult<Stream>(OpenTemporaryContent(contentPath)))
                : SvnCommitChange.ModifyFileStream(file.Path, _ => ValueTask.FromResult<Stream>(OpenTemporaryContent(contentPath)));
        }

        MergePropertyChanges(file.Path, SvnNodeKind.File, file.PropertyChanges);

        _files.Remove(command.Token);
    }

    private void ChangeFileProperty(ChangeFilePropertyEditorCommand command) {
        RequireFile(command.Token).PropertyChanges.Add(ToPropertyChange(command.Name, command.Value));
    }

    private async ValueTask<Stream> OpenFileAsync(SvnRevision revision, SvnRepositoryPath path, CancellationToken cancellationToken) {
        var root = await _repository.OpenRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        return await root.OpenFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureEditComplete() {
        if (_files.Count != 0 || _directories.Count != 0) { throw ProtocolError("close-edit was received while editor tokens are still open."); }
        if (_baseRevision is null) { throw ProtocolError("The editor root was never opened."); }
    }

    private DirectoryState RequireDirectory(string token) => _directories.TryGetValue(token, out var value) ? value : throw ProtocolError($"Unknown directory token '{token}'.");
    private FileState RequireFile(string token) => _files.TryGetValue(token, out var value) ? value : throw ProtocolError($"Unknown file token '{token}'.");
    private ISvnRevisionRoot BaseRoot => _baseRoot ?? throw ProtocolError("The editor root must be opened first.");
    private SvnRepositoryPath ResolvePath(SvnRepositoryPath path) => _anchorPath.Append(path);
    private SvnRepositoryPath ResolveCopyPath(string value) {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) { return new SvnRepositoryPath(value); }
        if (!_repositoryRootUri.IsBaseOf(uri)) { throw ProtocolError($"Copy source '{value}' is outside this repository."); }
        return new SvnRepositoryPath(Uri.UnescapeDataString(_repositoryRootUri.MakeRelativeUri(uri).ToString()));
    }
    private void RemoveChangesAtOrBelow(SvnRepositoryPath path) { foreach (var key in _changes.Keys.Where(key => key == path.Value || key.StartsWith(path.Value + "/", StringComparison.Ordinal)).ToArray()) { _changes.Remove(key); } }
    private void MergePropertyChanges(SvnRepositoryPath path, SvnNodeKind kind, IReadOnlyList<SvnPropertyChange> propertyChanges) {
        if (propertyChanges.Count == 0) { return; }
        _changes[path.Value] = _changes.TryGetValue(path.Value, out var change)
            ? change.WithPropertyChanges(propertyChanges)
            : SvnCommitChange.ModifyProperties(path, kind, propertyChanges);
    }
    private static SvnPropertyChange ToPropertyChange(string name, ReadOnlyMemory<byte>? value) => value is { } bytes ? SvnPropertyChange.Set(name, bytes.Span) : SvnPropertyChange.Delete(name);
    private static async ValueTask VerifyChecksumAsync(Stream content, string? expected, string label, CancellationToken cancellationToken) {
        if (expected is null) { content.Position = 0; return; }
        var actual = await CalculateChecksumAsync(content, cancellationToken).ConfigureAwait(false);
        VerifyChecksum(actual, expected, label);
    }
    private static void VerifyChecksum(string actual, string? expected, string label) { if (expected is not null && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) { throw ProtocolError($"The {label} checksum does not match: expected {expected}, actual {actual}."); } }
    private static async ValueTask<string> CalculateChecksumAsync(Stream content, CancellationToken cancellationToken) {
        content.Position = 0;
        var hash = await MD5.HashDataAsync(content, cancellationToken).ConfigureAwait(false);
        content.Position = 0;
        return Convert.ToHexStringLower(hash);
    }
    private static Stream OpenTemporaryContent(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static SvnWireProtocolException ProtocolError(string message) => new(message);
    private static void ValidateNodeRevision(SvnRevision? reported, SvnNodeInfo info) {
        if (reported is { } revision && info.LastChangedRevision.Value > revision.Value) { throw new SvnOutOfDateException(revision, info.LastChangedRevision); }
    }

    private void TraceDelta(FileStream delta, long sourceLength, long targetLength) {
        if (_trace is null) { return; }
        if (delta.Length > MaximumTracedDeltaSize) { _trace($"  svndiff: wire={delta.Length} bytes, source={sourceLength}, target={targetLength}; disassembly omitted for a large delta"); return; }
        delta.Position = 0;
        var bytes = new byte[checked((int)delta.Length)];
        delta.ReadExactly(bytes);
        delta.Position = 0;
        var inspection = SvnDiffInspector.Inspect(bytes);
        _trace($"  svndiff{(byte)inspection.Version}: wire={delta.Length} bytes, source={sourceLength}, target={targetLength}, windows={inspection.Windows.Count}");
        for (var index = 0; index < inspection.Windows.Count; index++) {
            var window = inspection.Windows[index];
            _trace($"    window {index}: source={window.SourceOffset}+{window.SourceLength}, target={window.TargetLength}, new-data={window.NewDataLength}, wire={window.WireLength}");
            foreach (var instruction in window.Instructions) { _trace($"      {instruction.Kind}: offset={instruction.Offset}, length={instruction.Length}"); }
        }
    }

    private static string Describe(RaSvnEditorCommand command) => command switch {
        TextDeltaChunkEditorCommand chunk => $"textdelta-chunk token={chunk.Token} bytes={chunk.Data.Length}",
        OpenRootEditorCommand value => $"open-root r{value.Revision?.Value} token={value.Token}",
        AddFileEditorCommand value => $"add-file {value.Path} token={value.Token}",
        OpenFileEditorCommand value => $"open-file {value.Path} token={value.Token}",
        DeleteEntryEditorCommand value => $"delete-entry {value.Path}",
        AddDirectoryEditorCommand value => $"add-dir {value.Path} token={value.Token}",
        ChangeFilePropertyEditorCommand value => $"change-file-prop token={value.Token} {value.Name}={(value.Value is null ? "delete" : $"{value.Value.Value.Length} bytes")}",
        ChangeDirectoryPropertyEditorCommand value => $"change-dir-prop token={value.Token} {value.Name}={(value.Value is null ? "delete" : $"{value.Value.Value.Length} bytes")}",
        _ => command.GetType().Name.Replace("EditorCommand", string.Empty, StringComparison.Ordinal)
    };

    public void Dispose() {
        foreach (var file in _files.Values) { file.Dispose(); }
        if (Directory.Exists(_temporaryPath)) { Directory.Delete(_temporaryPath, recursive: true); }
    }

    private sealed class DirectoryState(SvnRepositoryPath path, bool isRoot) {
        public SvnRepositoryPath Path { get; } = path;
        public bool IsRoot { get; } = isRoot;
        public List<SvnPropertyChange> PropertyChanges { get; } = [];
    }
    private sealed class FileState(SvnRepositoryPath path, bool isAdd, SvnCopySource? copyFrom = null) : IDisposable {
        public SvnRepositoryPath Path { get; } = path;
        public bool IsAdd { get; } = isAdd;
        public SvnCopySource? CopyFrom { get; } = copyFrom;
        public bool DeltaStarted { get; set; }
        public bool DeltaEnded { get; set; }
        public string? BaseChecksum { get; set; }
        public FileStream? Delta { get; private set; }
        public string? ContentPath { get; set; }
        public string? TargetChecksum { get; set; }
        public List<SvnPropertyChange> PropertyChanges { get; } = [];
        public FileStream GetDeltaStream(string temporaryPath) {
            if (Delta is not null) { return Delta; }
            Directory.CreateDirectory(temporaryPath);
            Delta = new FileStream(System.IO.Path.Combine(temporaryPath, Guid.NewGuid().ToString("N") + ".svndiff"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Delta;
        }
        public void Dispose() => Delta?.Dispose();
    }
}

internal readonly record struct CommitEditorResult(bool IsComplete, bool IsAborted, SvnRevision? Revision) {
    public static CommitEditorResult Continue => default;
    public static CommitEditorResult Aborted => new(true, true, null);
}

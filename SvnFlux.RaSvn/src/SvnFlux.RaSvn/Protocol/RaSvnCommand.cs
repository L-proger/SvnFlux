using SvnFlux.Core;
using SvnFlux.RaSvn.Wire;
using static SvnFlux.RaSvn.Protocol.SvnProtocolItems;

namespace SvnFlux.RaSvn.Protocol;

internal abstract record RaSvnCommand;
internal sealed record GetLatestRevisionCommand : RaSvnCommand;
internal sealed record CheckPathCommand(SvnRepositoryPath Path, SvnRevision? Revision) : RaSvnCommand;
internal sealed record StatCommand(SvnRepositoryPath Path, SvnRevision? Revision) : RaSvnCommand;
internal sealed record GetDirectoryCommand(SvnRepositoryPath Path, SvnRevision? Revision, bool WantProperties, bool WantContents) : RaSvnCommand;
internal sealed record GetFileCommand(SvnRepositoryPath Path, SvnRevision? Revision, bool WantProperties, bool WantContents) : RaSvnCommand;
internal sealed record GetLockCommand(SvnRepositoryPath Path) : RaSvnCommand;
internal sealed record GetLocksCommand(SvnRepositoryPath Path) : RaSvnCommand;
internal sealed record LockCommand(SvnRepositoryPath Path, string? Comment, bool StealLock, SvnRevision? CurrentRevision) : RaSvnCommand;
internal sealed record UnlockCommand(SvnRepositoryPath Path, string? Token, bool BreakLock) : RaSvnCommand;
internal sealed record LockManyCommand(string? Comment, bool StealLock, IReadOnlyList<(SvnRepositoryPath Path, SvnRevision? Revision)> Targets) : RaSvnCommand;
internal sealed record UnlockManyCommand(bool BreakLock, IReadOnlyList<(SvnRepositoryPath Path, string? Token)> Targets) : RaSvnCommand;
internal sealed record RevisionPropertyListCommand(SvnRevision Revision) : RaSvnCommand;
internal sealed record RevisionPropertyCommand(SvnRevision Revision, string Name) : RaSvnCommand;
internal sealed record ChangeRevisionPropertyCommand(SvnRevision Revision, string Name, ReadOnlyMemory<byte>? Value, bool IgnoreExpectedValue, ReadOnlyMemory<byte>? ExpectedValue) : RaSvnCommand;
internal sealed record GetFileRevisionsCommand(SvnRepositoryPath Path, SvnRevision? StartRevision, SvnRevision? EndRevision, bool IncludeMergedRevisions) : RaSvnCommand;
internal sealed record GetLocationsCommand(SvnRepositoryPath Path, SvnRevision PegRevision, IReadOnlyList<SvnRevision> Revisions) : RaSvnCommand;
internal sealed record GetLocationSegmentsCommand(SvnRepositoryPath Path, SvnRevision? PegRevision, SvnRevision? StartRevision, SvnRevision? EndRevision) : RaSvnCommand;
internal sealed record CommitCommand(string LogMessage, IReadOnlyDictionary<SvnRepositoryPath, string> LockTokens, bool KeepLocks) : RaSvnCommand;
internal sealed record UpdateCommand(
    SvnRevision? Revision,
    SvnRepositoryPath Target,
    bool Recurse,
    string Depth,
    bool SendCopyFromArguments,
    bool IgnoreAncestry) : RaSvnCommand;
internal sealed record StatusCommand(SvnRepositoryPath Target, bool Recurse, SvnRevision? Revision, string Depth) : RaSvnCommand;
internal sealed record DiffCommand(SvnRevision? Revision, SvnRepositoryPath Target, bool Recurse, bool IgnoreAncestry, Uri VersusUrl, bool TextDeltas, string Depth) : RaSvnCommand;
internal sealed record SwitchCommand(SvnRevision? Revision, SvnRepositoryPath Target, bool Recurse, Uri Url, string Depth, bool SendCopyFromArguments, bool IgnoreAncestry) : RaSvnCommand;
internal sealed record LogCommand(
    IReadOnlyList<SvnRepositoryPath> Paths,
    SvnRevision? StartRevision,
    SvnRevision? EndRevision,
    bool IncludeChangedPaths,
    int Limit) : RaSvnCommand;
internal sealed record ReparentCommand(Uri Uri) : RaSvnCommand;
internal sealed record UnknownCommand(string Name) : RaSvnCommand;

internal static class RaSvnCommandDecoder
{
    public static RaSvnCommand Decode(SvnWireItem item)
    {
        if (item is not SvnWireList { Items.Count: >= 2 } tuple ||
            tuple.Items[0] is not SvnWireWord name ||
            tuple.Items[1] is not SvnWireList parameters)
        {
            throw new SvnWireProtocolException("Expected a main-command tuple containing a command name and parameter list.");
        }

        try
        {
            return name.Value switch
            {
                "get-latest-rev" => RequireEmpty(parameters, new GetLatestRevisionCommand()),
                "check-path" => new CheckPathCommand(ReadPath(parameters, 0), ReadOptionalRevision(parameters, 1)),
                "stat" => new StatCommand(ReadPath(parameters, 0), ReadOptionalRevision(parameters, 1)),
                "get-dir" => DecodeGetDirectory(parameters),
                "get-file" => DecodeGetFile(parameters),
                "get-lock" => new GetLockCommand(ReadPath(parameters, 0)),
                "get-locks" => new GetLocksCommand(ReadPath(parameters, 0)),
                "lock" => DecodeLock(parameters),
                "unlock" => DecodeUnlock(parameters),
                "lock-many" => DecodeLockMany(parameters),
                "unlock-many" => DecodeUnlockMany(parameters),
                "rev-proplist" => new RevisionPropertyListCommand(ReadRevision(parameters, 0)),
                "rev-prop" => new RevisionPropertyCommand(ReadRevision(parameters, 0), GetText(parameters.Items[1], "property-name")),
                "change-rev-prop" => DecodeChangeRevisionProperty(parameters),
                "change-rev-prop2" => DecodeChangeRevisionProperty2(parameters),
                "get-file-revs" => DecodeGetFileRevisions(parameters),
                "get-locations" => DecodeGetLocations(parameters),
                "get-location-segments" => DecodeGetLocationSegments(parameters),
                "commit" => DecodeCommit(parameters),
                "update" => DecodeUpdate(parameters),
                "status" => DecodeStatus(parameters),
                "diff" => DecodeDiff(parameters),
                "switch" => DecodeSwitch(parameters),
                "log" => DecodeLog(parameters),
                "reparent" => DecodeReparent(parameters),
                _ => new UnknownCommand(name.Value)
            };
        }
        catch (ArgumentException exception)
        {
            throw new SvnWireProtocolException(exception.Message);
        }
    }

    private static GetDirectoryCommand DecodeGetDirectory(SvnWireList parameters)
    {
        RequireCount(parameters, 4, "get-dir");
        return new GetDirectoryCommand(
            ReadPath(parameters, 0),
            ReadOptionalRevision(parameters, 1),
            ReadBoolean(parameters, 2, "want-properties"),
            ReadBoolean(parameters, 3, "want-contents"));
    }

    private static LockCommand DecodeLock(SvnWireList parameters) {
        RequireCount(parameters, 3, "lock");
        return new LockCommand(ReadPath(parameters, 0), ReadOptionalText(parameters.Items[1], "comment"), ReadBoolean(parameters, 2, "steal-lock"), parameters.Items.Count > 3 ? ReadOptionalRevision(parameters, 3) : null);
    }

    private static UnlockCommand DecodeUnlock(SvnWireList parameters) {
        RequireCount(parameters, 3, "unlock");
        return new UnlockCommand(ReadPath(parameters, 0), ReadOptionalText(parameters.Items[1], "lock-token"), ReadBoolean(parameters, 2, "break-lock"));
    }

    private static LockManyCommand DecodeLockMany(SvnWireList parameters) {
        RequireCount(parameters, 3, "lock-many");
        if (parameters.Items[2] is not SvnWireList targets) { throw new SvnWireProtocolException("lock-many targets must be a list."); }
        var values = targets.Items.Select(item => {
            if (item is not SvnWireList { Items.Count: >= 2 } pair) { throw new SvnWireProtocolException("lock-many target is invalid."); }
            return (new SvnRepositoryPath(GetText(pair.Items[0], "path")), ReadOptionalRevision(pair, 1));
        }).ToArray();
        return new LockManyCommand(ReadOptionalText(parameters.Items[0], "comment"), ReadBoolean(parameters, 1, "steal-lock"), values);
    }

    private static UnlockManyCommand DecodeUnlockMany(SvnWireList parameters) {
        RequireCount(parameters, 2, "unlock-many");
        if (parameters.Items[1] is not SvnWireList targets) { throw new SvnWireProtocolException("unlock-many targets must be a list."); }
        var values = targets.Items.Select(item => {
            if (item is not SvnWireList { Items.Count: >= 2 } pair) { throw new SvnWireProtocolException("unlock-many target is invalid."); }
            return (new SvnRepositoryPath(GetText(pair.Items[0], "path")), ReadOptionalText(pair.Items[1], "lock-token"));
        }).ToArray();
        return new UnlockManyCommand(ReadBoolean(parameters, 0, "break-lock"), values);
    }

    private static GetFileCommand DecodeGetFile(SvnWireList parameters)
    {
        RequireCount(parameters, 4, "get-file");
        return new GetFileCommand(
            ReadPath(parameters, 0),
            ReadOptionalRevision(parameters, 1),
            ReadBoolean(parameters, 2, "want-properties"),
            ReadBoolean(parameters, 3, "want-contents"));
    }

    private static LogCommand DecodeLog(SvnWireList parameters)
    {
        RequireCount(parameters, 5, "log");
        if (parameters.Items[0] is not SvnWireList paths)
        {
            throw new SvnWireProtocolException("The log paths field must be a list.");
        }

        var decodedPaths = paths.Items.Select((path, index) => ReadPathItem(path, $"paths[{index}]")).ToArray();
        var limit = parameters.Items.Count > 5 && parameters.Items[5] is SvnWireNumber number
            ? checked((int)Math.Min(number.Value, int.MaxValue))
            : 0;

        return new LogCommand(
            decodedPaths,
            ReadOptionalRevision(parameters, 1),
            ReadOptionalRevision(parameters, 2),
            ReadBoolean(parameters, 3, "changed-paths"),
            limit);
    }

    private static UpdateCommand DecodeUpdate(SvnWireList parameters)
    {
        RequireCount(parameters, 3, "update");
        var depth = parameters.Items.Count > 3
            ? ReadWord(parameters.Items[3], "depth")
            : ReadBoolean(parameters, 2, "recurse") ? "infinity" : "files";
        var sendCopyFromArguments = parameters.Items.Count > 4 && ReadBoolean(parameters, 4, "send-copyfrom-args");
        var ignoreAncestry = parameters.Items.Count > 5 && ReadBoolean(parameters, 5, "ignore-ancestry");
        return new UpdateCommand(
            ReadOptionalRevision(parameters, 0),
            ReadPath(parameters, 1),
            ReadBoolean(parameters, 2, "recurse"),
            depth,
            sendCopyFromArguments,
            ignoreAncestry);
    }

    private static StatusCommand DecodeStatus(SvnWireList parameters) {
        RequireCount(parameters, 2, "status");
        var revision = parameters.Items.Count > 2 ? ReadOptionalRevision(parameters, 2) : null;
        var depth = parameters.Items.Count > 3 ? ReadWord(parameters.Items[3], "depth") : ReadBoolean(parameters, 1, "recurse") ? "infinity" : "immediates";
        return new StatusCommand(ReadPath(parameters, 0), ReadBoolean(parameters, 1, "recurse"), revision, depth);
    }

    private static DiffCommand DecodeDiff(SvnWireList parameters) {
        RequireCount(parameters, 5, "diff");
        var urlText = GetText(parameters.Items[4], "versus-url");
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) || url.Scheme != "svn") { throw new SvnWireProtocolException("The diff versus URL must be an absolute svn:// URL."); }
        var textDeltas = parameters.Items.Count <= 5 || ReadBoolean(parameters, 5, "text-deltas");
        var depth = parameters.Items.Count > 6 ? ReadWord(parameters.Items[6], "depth") : ReadBoolean(parameters, 2, "recurse") ? "infinity" : "files";
        return new DiffCommand(ReadOptionalRevision(parameters, 0), ReadPath(parameters, 1), ReadBoolean(parameters, 2, "recurse"), ReadBoolean(parameters, 3, "ignore-ancestry"), url, textDeltas, depth);
    }

    private static SwitchCommand DecodeSwitch(SvnWireList parameters) {
        RequireCount(parameters, 4, "switch");
        var urlText = GetText(parameters.Items[3], "switch-url");
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) || url.Scheme != "svn") { throw new SvnWireProtocolException("The switch URL must be an absolute svn:// URL."); }
        var depth = parameters.Items.Count > 4 ? ReadWord(parameters.Items[4], "depth") : ReadBoolean(parameters, 2, "recurse") ? "infinity" : "files";
        return new SwitchCommand(ReadOptionalRevision(parameters, 0), ReadPath(parameters, 1), ReadBoolean(parameters, 2, "recurse"), url, depth, parameters.Items.Count > 5 && ReadBoolean(parameters, 5, "send-copyfrom-args"), parameters.Items.Count > 6 && ReadBoolean(parameters, 6, "ignore-ancestry"));
    }

    private static CommitCommand DecodeCommit(SvnWireList parameters) {
        RequireCount(parameters, 1, "commit");
        var logMessage = GetText(parameters.Items[0], "log-message");
        var keepLocks = parameters.Items.Count > 2 && ReadBoolean(parameters, 2, "keep-locks");
        var lockTokens = new Dictionary<SvnRepositoryPath, string>();
        if (parameters.Items.Count > 1 && parameters.Items[1] is SvnWireList locks) {
            foreach (var item in locks.Items) {
                if (item is not SvnWireList { Items.Count: >= 2 } pair) { throw new SvnWireProtocolException("Commit lock token entry is invalid."); }
                lockTokens[new SvnRepositoryPath(GetText(pair.Items[0], "lock-path"))] = GetText(pair.Items[1], "lock-token");
            }
        }
        if (parameters.Items.Count > 3 && parameters.Items[3] is SvnWireList revisionProperties) {
            foreach (var item in revisionProperties.Items) {
                if (item is SvnWireList { Items.Count: >= 2 } pair && GetText(pair.Items[0], "revision-property-name") == "svn:log") {
                    logMessage = GetText(pair.Items[1], "svn:log");
                }
            }
        }

        return new CommitCommand(logMessage, lockTokens, keepLocks);
    }

    private static ChangeRevisionPropertyCommand DecodeChangeRevisionProperty(SvnWireList parameters) {
        RequireCount(parameters, 2, "change-rev-prop");
        return new ChangeRevisionPropertyCommand(ReadRevision(parameters, 0), GetText(parameters.Items[1], "property-name"), parameters.Items.Count > 2 ? ReadBytes(parameters.Items[2], "property-value") : null, true, null);
    }

    private static ChangeRevisionPropertyCommand DecodeChangeRevisionProperty2(SvnWireList parameters) {
        RequireCount(parameters, 4, "change-rev-prop2");
        var value = ReadOptionalBytes(parameters.Items[2], "property-value");
        if (parameters.Items[3] is not SvnWireList expected || expected.Items.Count < 1) { throw new SvnWireProtocolException("change-rev-prop2 expected-value field is invalid."); }
        var ignoreExpected = expected.Items[0] is SvnWireWord word && word.Value switch { "true" => true, "false" => false, _ => throw new SvnWireProtocolException("dont-care must be boolean.") };
        var previous = expected.Items.Count > 1 ? ReadBytes(expected.Items[1], "previous-value") : null;
        return new ChangeRevisionPropertyCommand(ReadRevision(parameters, 0), GetText(parameters.Items[1], "property-name"), value, ignoreExpected, previous);
    }

    private static GetFileRevisionsCommand DecodeGetFileRevisions(SvnWireList parameters) {
        RequireCount(parameters, 3, "get-file-revs");
        return new GetFileRevisionsCommand(
            ReadPath(parameters, 0),
            ReadOptionalRevision(parameters, 1),
            ReadOptionalRevision(parameters, 2),
            parameters.Items.Count > 3 && ReadBoolean(parameters, 3, "include-merged-revisions"));
    }

    private static GetLocationsCommand DecodeGetLocations(SvnWireList parameters) {
        RequireCount(parameters, 3, "get-locations");
        if (parameters.Items[2] is not SvnWireList revisions) { throw new SvnWireProtocolException("The get-locations revisions field must be a list."); }
        return new GetLocationsCommand(ReadPath(parameters, 0), ReadRevision(parameters, 1), revisions.Items.Select((item, index) => item is SvnWireNumber number ? new SvnRevision(number.Value) : throw new SvnWireProtocolException($"Location revision {index} must be a number.")).ToArray());
    }

    private static GetLocationSegmentsCommand DecodeGetLocationSegments(SvnWireList parameters) {
        RequireCount(parameters, 2, "get-location-segments");
        return new GetLocationSegmentsCommand(
            ReadPath(parameters, 0),
            ReadOptionalRevision(parameters, 1),
            parameters.Items.Count > 2 ? ReadOptionalRevision(parameters, 2) : null,
            parameters.Items.Count > 3 ? ReadOptionalRevision(parameters, 3) : null);
    }

    private static ReparentCommand DecodeReparent(SvnWireList parameters)
    {
        RequireCount(parameters, 1, "reparent");
        var text = GetText(parameters.Items[0], "url");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "svn")
        {
            throw new SvnWireProtocolException("The reparent URL must be an absolute svn:// URL.");
        }

        return new ReparentCommand(uri);
    }

    private static T RequireEmpty<T>(SvnWireList parameters, T command) where T : RaSvnCommand
    {
        if (parameters.Items.Count != 0)
        {
            throw new SvnWireProtocolException("This command does not accept parameters.");
        }

        return command;
    }

    private static SvnRepositoryPath ReadPath(SvnWireList parameters, int index)
    {
        RequireCount(parameters, index + 1, "command");
        return ReadPathItem(parameters.Items[index], "path");
    }

    private static SvnRepositoryPath ReadPathItem(SvnWireItem item, string fieldName) =>
        new(GetText(item, fieldName));

    private static SvnRevision ReadRevision(SvnWireList parameters, int index)
    {
        RequireCount(parameters, index + 1, "command");
        return parameters.Items[index] is SvnWireNumber number
            ? new SvnRevision(number.Value)
            : throw new SvnWireProtocolException("Expected a revision number.");
    }

    private static SvnRevision? ReadOptionalRevision(SvnWireList parameters, int index)
    {
        if (parameters.Items.Count <= index)
        {
            return null;
        }

        if (parameters.Items[index] is not SvnWireList optional)
        {
            throw new SvnWireProtocolException("An optional revision must be encoded as a list.");
        }

        return optional.Items.Count switch
        {
            0 => null,
            1 when optional.Items[0] is SvnWireNumber number => new SvnRevision(number.Value),
            _ => throw new SvnWireProtocolException("An optional revision list must be empty or contain one number.")
        };
    }

    private static bool ReadBoolean(SvnWireList parameters, int index, string fieldName)
    {
        RequireCount(parameters, index + 1, "command");
        return parameters.Items[index] is SvnWireWord word
            ? word.Value switch
            {
                "true" => true,
                "false" => false,
                _ => throw new SvnWireProtocolException($"Field '{fieldName}' must be true or false.")
            }
            : throw new SvnWireProtocolException($"Field '{fieldName}' must be a word.");
    }

    private static string ReadWord(SvnWireItem item, string fieldName) =>
        item is SvnWireWord word
            ? word.Value
            : throw new SvnWireProtocolException($"Field '{fieldName}' must be a word.");

    private static ReadOnlyMemory<byte> ReadBytes(SvnWireItem item, string fieldName) => item is SvnWireString value ? value.Value : throw new SvnWireProtocolException($"Field '{fieldName}' must be a string.");
    private static ReadOnlyMemory<byte>? ReadOptionalBytes(SvnWireItem item, string fieldName) {
        if (item is not SvnWireList optional) { throw new SvnWireProtocolException($"Field '{fieldName}' must be an optional tuple."); }
        return optional.Items.Count switch { 0 => (ReadOnlyMemory<byte>?)null, 1 => ReadBytes(optional.Items[0], fieldName), _ => throw new SvnWireProtocolException($"Field '{fieldName}' has invalid arity.") };
    }
    private static string? ReadOptionalText(SvnWireItem item, string fieldName) {
        if (item is not SvnWireList optional) { throw new SvnWireProtocolException($"Field '{fieldName}' must be an optional tuple."); }
        return optional.Items.Count switch { 0 => null, 1 => GetText(optional.Items[0], fieldName), _ => throw new SvnWireProtocolException($"Field '{fieldName}' has invalid arity.") };
    }

    private static void RequireCount(SvnWireList parameters, int minimum, string commandName)
    {
        if (parameters.Items.Count < minimum)
        {
            throw new SvnWireProtocolException($"Command '{commandName}' has too few parameters.");
        }
    }
}

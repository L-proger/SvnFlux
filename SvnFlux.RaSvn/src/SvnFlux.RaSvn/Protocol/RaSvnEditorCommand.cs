using SvnFlux.Core;
using SvnFlux.RaSvn.Wire;
using static SvnFlux.RaSvn.Protocol.SvnProtocolItems;

namespace SvnFlux.RaSvn.Protocol;

internal abstract record RaSvnEditorCommand;
internal sealed record OpenRootEditorCommand(SvnRevision? Revision, string Token) : RaSvnEditorCommand;
internal sealed record DeleteEntryEditorCommand(SvnRepositoryPath Path, SvnRevision? Revision, string ParentToken) : RaSvnEditorCommand;
internal sealed record AddDirectoryEditorCommand(SvnRepositoryPath Path, string ParentToken, string Token, string? CopyFromPath, SvnRevision? CopyFromRevision) : RaSvnEditorCommand;
internal sealed record OpenDirectoryEditorCommand(SvnRepositoryPath Path, string ParentToken, string Token, SvnRevision? Revision) : RaSvnEditorCommand;
internal sealed record ChangeDirectoryPropertyEditorCommand(string Token, string Name, ReadOnlyMemory<byte>? Value) : RaSvnEditorCommand;
internal sealed record CloseDirectoryEditorCommand(string Token) : RaSvnEditorCommand;
internal sealed record AddFileEditorCommand(SvnRepositoryPath Path, string ParentToken, string Token, string? CopyFromPath, SvnRevision? CopyFromRevision) : RaSvnEditorCommand;
internal sealed record OpenFileEditorCommand(SvnRepositoryPath Path, string ParentToken, string Token, SvnRevision? Revision) : RaSvnEditorCommand;
internal sealed record ApplyTextDeltaEditorCommand(string Token, string? BaseChecksum) : RaSvnEditorCommand;
internal sealed record TextDeltaChunkEditorCommand(string Token, ReadOnlyMemory<byte> Data) : RaSvnEditorCommand;
internal sealed record TextDeltaEndEditorCommand(string Token) : RaSvnEditorCommand;
internal sealed record ChangeFilePropertyEditorCommand(string Token, string Name, ReadOnlyMemory<byte>? Value) : RaSvnEditorCommand;
internal sealed record CloseFileEditorCommand(string Token, string? TextChecksum) : RaSvnEditorCommand;
internal sealed record CloseEditEditorCommand : RaSvnEditorCommand;
internal sealed record AbortEditEditorCommand : RaSvnEditorCommand;

internal static class RaSvnEditorCommandDecoder {
    public static RaSvnEditorCommand Decode(SvnWireItem item) {
        if (item is not SvnWireList { Items.Count: >= 2 } tuple || tuple.Items[0] is not SvnWireWord name || tuple.Items[1] is not SvnWireList parameters) {
            throw new SvnWireProtocolException("Expected an editor-command tuple containing a name and parameter list.");
        }

        return name.Value switch {
            "open-root" => new OpenRootEditorCommand(ReadOptionalRevision(parameters, 0), ReadToken(parameters, 1)),
            "delete-entry" => new DeleteEntryEditorCommand(ReadPath(parameters, 0), ReadOptionalRevision(parameters, 1), ReadToken(parameters, 2)),
            "add-dir" => DecodeAddDirectory(parameters),
            "open-dir" => new OpenDirectoryEditorCommand(ReadPath(parameters, 0), ReadToken(parameters, 1), ReadToken(parameters, 2), ReadOptionalRevision(parameters, 3)),
            "change-dir-prop" => new ChangeDirectoryPropertyEditorCommand(ReadToken(parameters, 0), ReadText(parameters.Items[1], "property-name"), ReadOptionalBytes(parameters, 2)),
            "close-dir" => new CloseDirectoryEditorCommand(ReadToken(parameters, 0)),
            "add-file" => DecodeAddFile(parameters),
            "open-file" => new OpenFileEditorCommand(ReadPath(parameters, 0), ReadToken(parameters, 1), ReadToken(parameters, 2), ReadOptionalRevision(parameters, 3)),
            "apply-textdelta" => new ApplyTextDeltaEditorCommand(ReadToken(parameters, 0), ReadOptionalText(parameters, 1)),
            "textdelta-chunk" => new TextDeltaChunkEditorCommand(ReadToken(parameters, 0), ReadBytes(parameters.Items[1], "delta-chunk")),
            "textdelta-end" => new TextDeltaEndEditorCommand(ReadToken(parameters, 0)),
            "change-file-prop" => new ChangeFilePropertyEditorCommand(ReadToken(parameters, 0), ReadText(parameters.Items[1], "property-name"), ReadOptionalBytes(parameters, 2)),
            "close-file" => new CloseFileEditorCommand(ReadToken(parameters, 0), ReadOptionalText(parameters, 1)),
            "close-edit" => RequireEmpty(parameters, new CloseEditEditorCommand()),
            "abort-edit" => RequireEmpty(parameters, new AbortEditEditorCommand()),
            _ => throw new SvnWireProtocolException($"Editor command '{name.Value}' is not supported.")
        };
    }

    private static AddDirectoryEditorCommand DecodeAddDirectory(SvnWireList parameters) {
        RequireCount(parameters, 4);
        var (copyPath, copyRevision) = ReadCopyFrom(parameters.Items[3]);
        return new AddDirectoryEditorCommand(ReadPath(parameters, 0), ReadToken(parameters, 1), ReadToken(parameters, 2), copyPath, copyRevision);
    }

    private static AddFileEditorCommand DecodeAddFile(SvnWireList parameters) {
        RequireCount(parameters, 4);
        var (copyPath, copyRevision) = ReadCopyFrom(parameters.Items[3]);
        return new AddFileEditorCommand(ReadPath(parameters, 0), ReadToken(parameters, 1), ReadToken(parameters, 2), copyPath, copyRevision);
    }

    private static (string? Path, SvnRevision? Revision) ReadCopyFrom(SvnWireItem item) {
        if (item is not SvnWireList optional) { throw new SvnWireProtocolException("Copy-from must be an optional tuple."); }
        return optional.Items.Count switch {
            0 => (null, null),
            2 when optional.Items[1] is SvnWireNumber revision => (ReadText(optional.Items[0], "copy-from-path"), new SvnRevision(revision.Value)),
            _ => throw new SvnWireProtocolException("Copy-from must be empty or contain path and revision.")
        };
    }

    private static SvnRevision? ReadOptionalRevision(SvnWireList parameters, int index) {
        RequireCount(parameters, index + 1);
        if (parameters.Items[index] is not SvnWireList optional) { throw new SvnWireProtocolException("Revision must be an optional tuple."); }
        return optional.Items.Count switch {
            0 => null,
            1 when optional.Items[0] is SvnWireNumber number => new SvnRevision(number.Value),
            _ => throw new SvnWireProtocolException("Revision tuple must be empty or contain one number.")
        };
    }

    private static string? ReadOptionalText(SvnWireList parameters, int index) {
        RequireCount(parameters, index + 1);
        if (parameters.Items[index] is not SvnWireList optional) { throw new SvnWireProtocolException("Text value must be an optional tuple."); }
        return optional.Items.Count switch { 0 => null, 1 => ReadText(optional.Items[0], "optional-text"), _ => throw new SvnWireProtocolException("Optional text tuple has invalid arity.") };
    }

    private static ReadOnlyMemory<byte>? ReadOptionalBytes(SvnWireList parameters, int index) {
        RequireCount(parameters, index + 1);
        if (parameters.Items[index] is not SvnWireList optional) { throw new SvnWireProtocolException("Binary value must be an optional tuple."); }
        return optional.Items.Count switch {
            0 => (ReadOnlyMemory<byte>?)null,
            1 when ReadBytes(optional.Items[0], "optional-bytes").IsEmpty => (ReadOnlyMemory<byte>?)null,
            1 => ReadBytes(optional.Items[0], "optional-bytes"),
            _ => throw new SvnWireProtocolException("Optional binary tuple has invalid arity.")
        };
    }

    private static SvnRepositoryPath ReadPath(SvnWireList parameters, int index) { RequireCount(parameters, index + 1); return new SvnRepositoryPath(ReadText(parameters.Items[index], "path")); }
    private static string ReadToken(SvnWireList parameters, int index) { RequireCount(parameters, index + 1); return ReadText(parameters.Items[index], "token"); }
    private static string ReadText(SvnWireItem item, string fieldName) => GetText(item, fieldName);
    private static ReadOnlyMemory<byte> ReadBytes(SvnWireItem item, string fieldName) => item is SvnWireString value ? value.Value.ToArray() : throw new SvnWireProtocolException($"Field '{fieldName}' must be a string.");
    private static T RequireEmpty<T>(SvnWireList parameters, T command) where T : RaSvnEditorCommand { if (parameters.Items.Count != 0) { throw new SvnWireProtocolException("This editor command takes no parameters."); } return command; }
    private static void RequireCount(SvnWireList parameters, int count) { if (parameters.Items.Count < count) { throw new SvnWireProtocolException("Editor command has too few parameters."); } }
}

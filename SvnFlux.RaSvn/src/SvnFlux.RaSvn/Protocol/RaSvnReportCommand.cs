using SvnFlux.Core;
using SvnFlux.RaSvn.Wire;
using static SvnFlux.RaSvn.Protocol.SvnProtocolItems;

namespace SvnFlux.RaSvn.Protocol;

internal abstract record RaSvnReportCommand;
internal sealed record SetPathReportCommand(
    SvnRepositoryPath Path,
    SvnRevision Revision,
    bool StartEmpty,
    string Depth) : RaSvnReportCommand;
internal sealed record DeletePathReportCommand(SvnRepositoryPath Path) : RaSvnReportCommand;
internal sealed record FinishReportCommand : RaSvnReportCommand;
internal sealed record AbortReportCommand : RaSvnReportCommand;
internal sealed record UnsupportedReportCommand(string Name) : RaSvnReportCommand;

internal static class RaSvnReportCommandDecoder
{
    public static RaSvnReportCommand Decode(SvnWireItem item)
    {
        if (item is not SvnWireList { Items.Count: >= 2 } tuple ||
            tuple.Items[0] is not SvnWireWord name ||
            tuple.Items[1] is not SvnWireList parameters)
        {
            throw new SvnWireProtocolException("Expected a report-command tuple containing a name and parameter list.");
        }

        return name.Value switch
        {
            "set-path" => DecodeSetPath(parameters),
            "delete-path" => new DeletePathReportCommand(ReadPath(parameters, 0)),
            "finish-report" => RequireEmpty(parameters, new FinishReportCommand()),
            "abort-report" => RequireEmpty(parameters, new AbortReportCommand()),
            _ => new UnsupportedReportCommand(name.Value)
        };
    }

    private static SetPathReportCommand DecodeSetPath(SvnWireList parameters)
    {
        RequireCount(parameters, 3, "set-path");
        var revision = parameters.Items[1] is SvnWireNumber number
            ? new SvnRevision(number.Value)
            : throw new SvnWireProtocolException("The set-path revision must be a number.");
        var startEmpty = parameters.Items[2] is SvnWireWord boolean
            ? boolean.Value switch
            {
                "true" => true,
                "false" => false,
                _ => throw new SvnWireProtocolException("The set-path start-empty field must be true or false.")
            }
            : throw new SvnWireProtocolException("The set-path start-empty field must be a word.");
        var depth = parameters.Items.Count > 4 && parameters.Items[4] is SvnWireWord depthWord
            ? depthWord.Value
            : "infinity";
        return new SetPathReportCommand(ReadPath(parameters, 0), revision, startEmpty, depth);
    }

    private static SvnRepositoryPath ReadPath(SvnWireList parameters, int index)
    {
        RequireCount(parameters, index + 1, "report command");
        return new SvnRepositoryPath(GetText(parameters.Items[index], "path"));
    }

    private static T RequireEmpty<T>(SvnWireList parameters, T command) where T : RaSvnReportCommand
    {
        if (parameters.Items.Count != 0)
        {
            throw new SvnWireProtocolException("This report command does not accept parameters.");
        }

        return command;
    }

    private static void RequireCount(SvnWireList parameters, int minimum, string commandName)
    {
        if (parameters.Items.Count < minimum)
        {
            throw new SvnWireProtocolException($"Command '{commandName}' has too few parameters.");
        }
    }
}

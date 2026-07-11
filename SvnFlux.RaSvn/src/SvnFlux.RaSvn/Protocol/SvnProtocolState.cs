namespace SvnFlux.RaSvn.Protocol;

public enum SvnProtocolState
{
    Handshake,
    Authentication,
    MainCommands,
    ReportInput,
    EditorInput,
    EditorOutput,
    Closed,
    Failed
}

namespace SvnFlux.Svndiff;

public enum SvnDiffInstructionKind
{
    Source,
    Target,
    NewData
}

public sealed record SvnDiffInstruction(SvnDiffInstructionKind Kind, int Offset, int Length);

public sealed record SvnDiffWindow(
    int SourceViewLength,
    int TargetViewLength,
    IReadOnlyList<SvnDiffInstruction> Instructions,
    ReadOnlyMemory<byte> NewData);

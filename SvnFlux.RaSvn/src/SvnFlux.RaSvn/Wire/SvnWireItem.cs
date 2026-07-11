namespace SvnFlux.RaSvn.Wire;

public abstract record SvnWireItem;

public sealed record SvnWireNumber(long Value) : SvnWireItem;

public sealed record SvnWireWord(string Value) : SvnWireItem;

public sealed record SvnWireString(ReadOnlyMemory<byte> Value) : SvnWireItem;

public sealed record SvnWireList(IReadOnlyList<SvnWireItem> Items) : SvnWireItem;

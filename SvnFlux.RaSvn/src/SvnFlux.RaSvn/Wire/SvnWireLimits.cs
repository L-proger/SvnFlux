namespace SvnFlux.RaSvn.Wire;

public sealed record SvnWireLimits
{
    public int MaximumStringLength { get; init; } = 16 * 1024 * 1024;
    public int MaximumWordLength { get; init; } = 31;
    public int MaximumListDepth { get; init; } = 32;
    public int MaximumItemsPerList { get; init; } = 16_384;
}

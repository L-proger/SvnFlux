namespace SvnFlux.Core;

public readonly record struct SvnRevision
{
    public SvnRevision(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public long Value { get; }

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

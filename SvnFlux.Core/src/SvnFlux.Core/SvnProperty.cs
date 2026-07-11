namespace SvnFlux.Core;

public sealed record SvnProperty
{
    private readonly byte[] _value;

    public SvnProperty(string name, ReadOnlySpan<byte> value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException("Property names must not contain control characters.", nameof(name));
        }

        Name = name;
        _value = value.ToArray();
    }

    public string Name { get; }
    public ReadOnlyMemory<byte> Value => _value;
}

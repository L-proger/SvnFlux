namespace SvnFlux.Core;

public sealed class SvnPropertyCollection : IReadOnlyCollection<SvnProperty>
{
    private readonly IReadOnlyList<SvnProperty> _items;

    public SvnPropertyCollection(IEnumerable<SvnProperty> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var items = properties.ToArray();
        if (items.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != items.Length)
        {
            throw new ArgumentException("Property names must be unique.", nameof(properties));
        }

        _items = items;
    }

    public static SvnPropertyCollection Empty { get; } = new([]);
    public int Count => _items.Count;
    public IEnumerator<SvnProperty> GetEnumerator() => _items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

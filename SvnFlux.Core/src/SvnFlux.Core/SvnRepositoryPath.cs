namespace SvnFlux.Core;

public readonly record struct SvnRepositoryPath
{
    public SvnRepositoryPath(string value)
    {
        Value = SvnRepositoryPathRules.Normalize(value);
    }

    public string Value { get; }

    public bool IsRoot => Value.Length == 0;

    public SvnRepositoryPath Append(SvnRepositoryPath relativePath) =>
        IsRoot ? relativePath : relativePath.IsRoot ? this : new($"{Value}/{relativePath.Value}");

    public override string ToString() => Value;
}

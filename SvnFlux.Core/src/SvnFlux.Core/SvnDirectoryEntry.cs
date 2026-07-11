namespace SvnFlux.Core;

public sealed record SvnDirectoryEntry(string Name, SvnNodeInfo NodeInfo)
{
    public string Name { get; } = ValidateName(Name);

    private static string ValidateName(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (value is "." or ".." || value.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw new ArgumentException("A directory entry name must be one canonical repository path segment.", nameof(value));
        }

        return value;
    }
}

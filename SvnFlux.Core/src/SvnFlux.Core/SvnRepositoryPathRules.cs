namespace SvnFlux.Core;

public static class SvnRepositoryPathRules
{
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.IndexOfAny(['\0', '\\']) >= 0)
        {
            throw new ArgumentException("Repository paths must not contain NUL or backslash characters.", nameof(value));
        }

        if (value.Length == 0 || value == "/")
        {
            return string.Empty;
        }

        var start = value[0] == '/' ? 1 : 0;
        var end = value[^1] == '/' ? value.Length - 1 : value.Length;
        if (start == end)
        {
            throw new ArgumentException("Repository paths must not contain duplicate separators.", nameof(value));
        }

        var normalized = value[start..end];
        foreach (var segment in normalized.Split('/', StringSplitOptions.None))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new ArgumentException("Repository paths must not contain empty, '.' or '..' segments.", nameof(value));
            }
        }

        return normalized;
    }
}

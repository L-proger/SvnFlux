namespace SvnFlux.Core;

public sealed record SvnNodeInfo(
    SvnNodeKind Kind,
    long Size,
    bool HasProperties,
    SvnRevision LastChangedRevision,
    DateTimeOffset? LastChangedTime = null,
    string? LastChangedAuthor = null,
    SvnChecksum? ContentChecksum = null);

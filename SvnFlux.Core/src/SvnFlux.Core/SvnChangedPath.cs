namespace SvnFlux.Core;

public enum SvnChangeAction
{
    Add,
    Delete,
    Replace,
    Modify
}

public sealed record SvnChangedPath(
    SvnRepositoryPath Path,
    SvnChangeAction Action,
    SvnNodeKind NodeKind,
    bool TextModified,
    bool PropertiesModified,
    SvnRepositoryPath? CopyFromPath = null,
    SvnRevision? CopyFromRevision = null);

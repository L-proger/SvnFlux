namespace SvnFlux.Core;

public sealed record SvnLogEntry(
    SvnRevision Revision,
    SvnRevisionProperties RevisionProperties,
    IReadOnlyList<SvnChangedPath> ChangedPaths);

public sealed record SvnLogQuery(
    IReadOnlyList<SvnRepositoryPath> Paths,
    SvnRevision StartRevision,
    SvnRevision EndRevision,
    int Limit = 0);

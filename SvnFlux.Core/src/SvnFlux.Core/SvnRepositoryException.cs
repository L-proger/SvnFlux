namespace SvnFlux.Core;

public abstract class SvnRepositoryException : Exception
{
    protected SvnRepositoryException(string message) : base(message)
    {
    }
}

public sealed class SvnPathNotFoundException : SvnRepositoryException
{
    public SvnPathNotFoundException(SvnRepositoryPath path) : base($"Repository path '{path}' was not found.")
    {
        Path = path;
    }

    public SvnRepositoryPath Path { get; }
}

public sealed class SvnNodeKindMismatchException : SvnRepositoryException
{
    public SvnNodeKindMismatchException(SvnRepositoryPath path, SvnNodeKind expectedKind) :
        base($"Repository path '{path}' is not a {expectedKind} node.")
    {
    }
}

public sealed class SvnInvalidRevisionException : SvnRepositoryException
{
    public SvnInvalidRevisionException(SvnRevision revision) : base($"Revision {revision} does not exist.")
    {
    }
}

public sealed class SvnOutOfDateException : SvnRepositoryException {
    public SvnOutOfDateException(SvnRevision expected, SvnRevision actual)
        : base($"Commit base revision {expected} is out of date; the youngest revision is {actual}.") { }
}

public sealed class SvnRevisionPropertyConflictException : SvnRepositoryException {
    public SvnRevisionPropertyConflictException(SvnRevision revision, string name) : base($"Revision property '{name}' on r{revision.Value} no longer has the expected value.") { }
}

public sealed class SvnLockException : SvnRepositoryException {
    public SvnLockException(SvnRepositoryPath path, string message) : base($"Lock for '{path}': {message}") { }
}

public sealed class SvnRepositoryBusyException : SvnRepositoryException {
    public SvnRepositoryBusyException() : base("Another process is currently writing this repository.") { }
}

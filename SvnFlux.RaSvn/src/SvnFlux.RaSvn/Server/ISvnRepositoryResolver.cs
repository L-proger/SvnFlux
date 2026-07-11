using SvnFlux.Core;

namespace SvnFlux.RaSvn.Server;

public interface ISvnRepositoryResolver
{
    ValueTask<SvnResolvedRepository?> ResolveAsync(
        Uri repositoryUri,
        CancellationToken cancellationToken = default);
}

public sealed record SvnResolvedRepository(
    ISvnRepository Repository,
    Uri RepositoryRootUri,
    SvnRepositoryPath SessionPath);

public sealed class SvnSingleRepositoryResolver : ISvnRepositoryResolver
{
    private readonly string _repositoryName;
    private readonly ISvnRepository _repository;

    public SvnSingleRepositoryResolver(string repositoryName, ISvnRepository repository)
    {
        ArgumentException.ThrowIfNullOrEmpty(repositoryName);
        if (repositoryName is "." or ".." || repositoryName.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw new ArgumentException("The repository name must be one URL path segment.", nameof(repositoryName));
        }

        ArgumentNullException.ThrowIfNull(repository);
        _repositoryName = repositoryName;
        _repository = repository;
    }

    public ValueTask<SvnResolvedRepository?> ResolveAsync(Uri repositoryUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryUri);
        cancellationToken.ThrowIfCancellationRequested();

        var decodedPath = Uri.UnescapeDataString(repositoryUri.AbsolutePath).Trim('/');
        var separator = decodedPath.IndexOf('/');
        var repositoryName = separator < 0 ? decodedPath : decodedPath[..separator];
        if (!string.Equals(repositoryName, _repositoryName, StringComparison.Ordinal))
        {
            return ValueTask.FromResult<SvnResolvedRepository?>(null);
        }

        var sessionPath = separator < 0 ? new SvnRepositoryPath(string.Empty) : new SvnRepositoryPath(decodedPath[(separator + 1)..]);
        var root = new UriBuilder(repositoryUri)
        {
            Path = "/" + Uri.EscapeDataString(_repositoryName),
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri;

        return ValueTask.FromResult<SvnResolvedRepository?>(new SvnResolvedRepository(_repository, root, sessionPath));
    }
}

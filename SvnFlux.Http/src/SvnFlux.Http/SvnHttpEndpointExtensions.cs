using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SvnFlux.Core;

namespace SvnFlux.Http;

public static class SvnHttpEndpointExtensions {
    public static IServiceCollection AddSvnFluxHttp(this IServiceCollection services, Action<SvnHttpOptions>? configure = null) {
        services.AddOptions<SvnHttpOptions>();
        if (configure is not null) services.Configure(configure);
        return services;
    }

    public static IEndpointConventionBuilder MapSvnRepository(this IEndpointRouteBuilder endpoints, string pattern, ISvnRepository repository) {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentNullException.ThrowIfNull(repository);
        pattern = pattern.TrimEnd('/');
        return endpoints.MapMethods(pattern + "/{**svnPath}", SvnHttpServer.Methods,
            (HttpContext context, string? svnPath) => SvnHttpServer.HandleAsync(context, repository, svnPath));
    }

    public static IEndpointConventionBuilder MapSvnRepositories(this IEndpointRouteBuilder endpoints, string pattern,
        Func<HttpContext, string, CancellationToken, ValueTask<ISvnRepository?>> resolver) {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentNullException.ThrowIfNull(resolver);
        pattern = pattern.TrimEnd('/');
        return endpoints.MapMethods(pattern + "/{repositoryName}/{**svnPath}", SvnHttpServer.Methods,
            async (HttpContext context, string repositoryName, string? svnPath) => {
                if (!IsRepositoryName(repositoryName)) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
                var repository = await resolver(context, repositoryName, context.RequestAborted).ConfigureAwait(false);
                if (repository is null) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
                await SvnHttpServer.HandleAsync(context, repository, svnPath).ConfigureAwait(false);
            });
    }

    private static bool IsRepositoryName(string value) =>
        value.Length > 0 && value is not ("." or "..") && value.IndexOfAny(['/', '\\', '\0']) < 0;
}

using System.CommandLine;
using SvnFlux.Core;
using SvnFlux.RaSvn.Server;
using SvnFlux.Repository.FileSystem;

namespace SvnFlux.Playground.Scenarios;

internal static class RaSvnScenario
{
    public static Command CreateCommand()
    {
        var portOption = new Option<int?>("--port")
        {
            Description = "TCP port to listen on. Defaults to 3690."
        };
        portOption.Aliases.Add("-p");

        var repositoryOption = new Option<string?>("--repository")
        {
            Description = "Repository name exposed in the svn:// URL. Defaults to 'repository'."
        };
        repositoryOption.Aliases.Add("-r");

        var pathOption = new Option<string?>("--path")
        {
            Description = "Filesystem repository directory. Defaults to '.playground-data/rasvn-repository'."
        };
        var publishUpdateOption = new Option<bool>("--publish-update")
        {
            Description = "Publish one new revision before starting the server, for exercising svn update."
        };
        var traceOption = new Option<bool>("--trace") {
            Description = "Print decoded ra_svn editor commands and svndiff instructions."
        };

        var command = new Command("rasvn", "Run the native svn:// protocol scenario over a filesystem repository.");
        command.Options.Add(portOption);
        command.Options.Add(repositoryOption);
        command.Options.Add(pathOption);
        command.Options.Add(publishUpdateOption);
        command.Options.Add(traceOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var port = parseResult.GetValue(portOption) ?? 3690;
            var repositoryName = parseResult.GetValue(repositoryOption) ?? "repository";
            var repositoryPath = parseResult.GetValue(pathOption) ?? Path.Combine(".playground-data", "rasvn-repository");
            var publishUpdate = parseResult.GetValue(publishUpdateOption);
            var trace = parseResult.GetValue(traceOption);
            return await RunAsync(port, repositoryName, repositoryPath, publishUpdate, trace, cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task<int> RunAsync(
        int port,
        string repositoryName,
        string repositoryPath,
        bool publishUpdate,
        bool trace,
        CancellationToken cancellationToken)
    {
        var fullRepositoryPath = Path.GetFullPath(repositoryPath);
        var repository = await OpenOrCreateRepositoryAsync(fullRepositoryPath, cancellationToken).ConfigureAwait(false);
        if (publishUpdate)
        {
            await PublishUpdateAsync(repository, cancellationToken).ConfigureAwait(false);
        }

        var latestRevision = await repository.GetLatestRevisionAsync(cancellationToken).ConfigureAwait(false);
        var resolver = new SvnSingleRepositoryResolver(repositoryName, repository);
        await using var server = new SvnServer(
            resolver,
            new SvnServerOptions
            {
                Port = port,
                DiagnosticLog = message => Console.Error.WriteLine($"[ra_svn] {message}"),
                ProtocolTrace = trace ? message => Console.WriteLine($"[trace] {message}") : null
            });

        Console.WriteLine($"SvnFlux ra_svn scenario listening at svn://localhost:{port}/{repositoryName}");
        Console.WriteLine($"Filesystem repository: {fullRepositoryPath}");
        Console.WriteLine($"Youngest revision: {latestRevision.Value}. Press Ctrl+C to stop.");
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static async ValueTask<SvnFileSystemRepository> OpenOrCreateRepositoryAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(Path.Combine(repositoryPath, "format.json")))
        {
            return await SvnFileSystemRepository.OpenAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        }

        var repository = await SvnFileSystemRepository.CreateAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        await repository.CreateRevisionAsync(
            [SvnFileSystemChange.Write(new SvnRepositoryPath("readme.txt"), "Hello from SvnFlux filesystem repository.\n"u8)],
            new SvnRevisionProperties(
                "SvnFlux.Playground",
                DateTimeOffset.UtcNow,
                "Add readme.txt",
                SvnPropertyCollection.Empty),
            cancellationToken).ConfigureAwait(false);

        await repository.CreateRevisionAsync(
            [SvnFileSystemChange.Write(new SvnRepositoryPath("revision-2.txt"), "Revision 2 keeps readme.txt unchanged.\n"u8)],
            new SvnRevisionProperties(
                "SvnFlux.Playground",
                DateTimeOffset.UtcNow,
                "Add revision-2 marker and reuse readme.txt body",
                SvnPropertyCollection.Empty),
            cancellationToken).ConfigureAwait(false);
        return repository;
    }

    private static async ValueTask PublishUpdateAsync(
        SvnFileSystemRepository repository,
        CancellationToken cancellationToken)
    {
        var current = await repository.GetLatestRevisionAsync(cancellationToken).ConfigureAwait(false);
        var nextRevision = current.Value + 1;
        await repository.CreateRevisionAsync(
            [
                SvnFileSystemChange.Write(
                    new SvnRepositoryPath("readme.txt"),
                    System.Text.Encoding.UTF8.GetBytes($"Hello from SvnFlux revision {nextRevision}.\n")),
                SvnFileSystemChange.Write(
                    new SvnRepositoryPath($"updates/revision-{nextRevision}.txt"),
                    System.Text.Encoding.UTF8.GetBytes($"Created for svn update to revision {nextRevision}.\n"))
            ],
            new SvnRevisionProperties(
                "SvnFlux.Playground",
                DateTimeOffset.UtcNow,
                $"Publish Playground update r{nextRevision}",
                SvnPropertyCollection.Empty),
            cancellationToken).ConfigureAwait(false);
    }
}

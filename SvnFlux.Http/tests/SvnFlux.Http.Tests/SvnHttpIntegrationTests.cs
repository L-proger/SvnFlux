using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SvnFlux.Core;
using SvnFlux.Repository.Memory;
using SvnFlux.Repository.FileSystem;

namespace SvnFlux.Http.Tests;

public sealed class SvnHttpIntegrationTests {
    [Fact]
    public async Task OptionsAdvertisesHttpV2Resources() {
        await using var server = await TestServer.StartAsync();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var request = new HttpRequestMessage(HttpMethod.Options, server.Url);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2", response.Headers.GetValues("SVN-Youngest-Rev").Single());
        Assert.EndsWith("/!svn/me", response.Headers.GetValues("SVN-Me-Resource").Single());
        Assert.EndsWith("/!svn/rvr", response.Headers.GetValues("SVN-Rev-Root-Stub").Single());
    }

    [Fact]
    public async Task EncodedPathSeparatorsAreRejected() {
        await using var server = await TestServer.StartAsync();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var request = new HttpRequestMessage(HttpMethod.Options, server.Url + "/a%2Fb");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OfficialClientCanInfoListAndCatFromMemory() {
        await using var server = await TestServer.StartAsync();
        await AssertOfficialClientAsync(server);
    }

    [Fact]
    public async Task OfficialClientCanInfoListAndCatFromFileSystem() {
        var path = Path.Combine(Path.GetTempPath(), "svnflux-http-repository-" + Guid.NewGuid().ToString("N"));
        try {
            var repository = await SvnFileSystemRepository.CreateAsync(path);
            await using var server = await TestServer.StartAsync(repository);
            await AssertOfficialClientAsync(server);
        } finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task OfficialClientCanResolveMultipleRepositories() {
        var first = new SvnMemoryRepository();
        var second = new SvnMemoryRepository();
        var properties = new SvnRevisionProperties("tester", DateTimeOffset.Parse("2026-07-12T00:00:00Z"), "initial", SvnPropertyCollection.Empty);
        await first.CommitAsync(new(new SvnRevision(0), properties, [SvnCommitChange.AddFile(new("readme.txt"), "first"u8)]));
        await second.CommitAsync(new(new SvnRevision(0), properties, [SvnCommitChange.AddFile(new("readme.txt"), "second"u8)]));
        await using var server = await TestServer.StartManyAsync(new Dictionary<string, ISvnRepository> { ["first"] = first, ["second"] = second });

        Assert.Equal("first", await RunSvnAsync(server, "cat", server.Url + "/first/readme.txt"));
        Assert.Equal("second", await RunSvnAsync(server, "cat", server.Url + "/second/readme.txt"));
    }

    private static async Task AssertOfficialClientAsync(TestServer server) {
        var info = await RunSvnAsync(server, "info", server.Url);
        var list = await RunSvnAsync(server, "list", server.Url);
        var cat = await RunSvnAsync(server, "cat", server.Url + "/readme.txt");
        var log = await RunSvnAsync(server, "log", "-v", server.Url);
        var limitedLog = await RunSvnAsync(server, "log", "-r", "1:1", "--limit", "1", server.Url);

        Assert.Contains("Revision: 2", info);
        Assert.Contains("readme.txt", list);
        Assert.Contains("src/", list);
        Assert.Equal("hello over http", cat);
        Assert.Contains("r2 | tester", log);
        Assert.Contains("M /readme.txt", log);
        Assert.Contains("A /docs.txt", log);
        Assert.Contains("r1 | tester", log);
        Assert.Contains("r1 | tester", limitedLog);
        Assert.DoesNotContain("r2 | tester", limitedLog);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfficialClientCanCheckoutAndUpdate(bool fileSystem) {
        var root = Path.Combine(Path.GetTempPath(), "svnflux-http-update-" + Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(root, "repository");
        var workingCopy = Path.Combine(root, "working-copy");
        Directory.CreateDirectory(root);
        try {
            ISvnWritableRepository repository = fileSystem
                ? await SvnFileSystemRepository.CreateAsync(repositoryPath)
                : new SvnMemoryRepository();
            await using (var server = await TestServer.StartAsync(repository)) {
                var large = Enumerable.Range(0, 200_003).Select(value => (byte)(value * 31)).ToArray();
                await RunSvnAsync(server, "checkout", server.Url, workingCopy);
                Assert.Equal("hello over http", await File.ReadAllTextAsync(Path.Combine(workingCopy, "readme.txt")));
                Assert.True(Directory.Exists(Path.Combine(workingCopy, ".svn")));
                Assert.Equal("v1", await RunSvnAsync(server, "propget", "demo", Path.Combine(workingCopy, "readme.txt")));

                var revision = await repository.GetLatestRevisionAsync();
                await repository.CommitAsync(new(revision, new("tester", DateTimeOffset.Parse("2026-07-12T02:00:00Z"), "update", SvnPropertyCollection.Empty), [
                    SvnCommitChange.ModifyFile(new("readme.txt"), "updated over http"u8).WithPropertyChanges([SvnPropertyChange.Set("demo", "v2"u8)]),
                    SvnCommitChange.AddFile(new("large.bin"), large),
                    SvnCommitChange.AddFile(new("new.txt"), "new file"u8),
                    SvnCommitChange.Delete(new("docs.txt"), SvnNodeKind.File)
                ]));
                await RunSvnAsync(server, "update", workingCopy);
                Assert.Equal("updated over http", await File.ReadAllTextAsync(Path.Combine(workingCopy, "readme.txt")));
                Assert.Equal("new file", await File.ReadAllTextAsync(Path.Combine(workingCopy, "new.txt")));
                Assert.False(File.Exists(Path.Combine(workingCopy, "docs.txt")));
                Assert.Equal("v2", await RunSvnAsync(server, "propget", "demo", Path.Combine(workingCopy, "readme.txt")));
                Assert.Equal(large, await File.ReadAllBytesAsync(Path.Combine(workingCopy, "large.bin")));
            }
        } finally { DeleteDirectory(root); }
    }


    [Fact]
    public async Task OfficialClientCanTargetUpdateMixedWorkingCopyAndReportRemoteStatus() {
        var root = Path.Combine(Path.GetTempPath(), "svnflux-http-mixed-" + Guid.NewGuid().ToString("N"));
        var workingCopy = Path.Combine(root, "working-copy");
        Directory.CreateDirectory(root);
        try {
            var repository = new SvnMemoryRepository();
            await using var server = await TestServer.StartAsync(repository);
            await RunSvnAsync(server, "checkout", server.Url, workingCopy);

            var revision = await repository.GetLatestRevisionAsync();
            revision = await repository.CommitAsync(new(revision, RevisionProperties("third"), [
                SvnCommitChange.ModifyFile(new("readme.txt"), "targeted"u8),
                SvnCommitChange.ModifyFile(new("src/code.cs"), "class C { int Value; }"u8)
            ]));
            await RunSvnAsync(server, "update", Path.Combine(workingCopy, "readme.txt"));
            Assert.Equal("targeted", await File.ReadAllTextAsync(Path.Combine(workingCopy, "readme.txt")));
            Assert.Equal("class C {}", await File.ReadAllTextAsync(Path.Combine(workingCopy, "src", "code.cs")));

            var status = await RunSvnAsync(server, "status", "-u", workingCopy);
            Assert.True(status.Contains('*'), status + "\nHTTP trace:\n" + string.Join('\n', server.Trace));
            Assert.Contains("code.cs", status);

            await repository.CommitAsync(new(revision, RevisionProperties("fourth"), [
                SvnCommitChange.ModifyFile(new("docs.txt"), "new documentation"u8)
            ]));
            await RunSvnAsync(server, "update", workingCopy);
            Assert.Equal("class C { int Value; }", await File.ReadAllTextAsync(Path.Combine(workingCopy, "src", "code.cs")));
            Assert.Equal("new documentation", await File.ReadAllTextAsync(Path.Combine(workingCopy, "docs.txt")));
        } finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task OfficialClientHonorsCheckoutDepth() {
        var root = Path.Combine(Path.GetTempPath(), "svnflux-http-depth-" + Guid.NewGuid().ToString("N"));
        var workingCopy = Path.Combine(root, "working-copy");
        Directory.CreateDirectory(root);
        try {
            await using var server = await TestServer.StartAsync();
            await RunSvnAsync(server, "checkout", "--depth", "files", server.Url, workingCopy);

            Assert.True(File.Exists(Path.Combine(workingCopy, "readme.txt")));
            Assert.True(File.Exists(Path.Combine(workingCopy, "docs.txt")));
            Assert.False(Directory.Exists(Path.Combine(workingCopy, "src")));
        } finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task OfficialClientCanSwitchReadUrlDiffAndCheckoutCopy() {
        var root = Path.Combine(Path.GetTempPath(), "svnflux-http-switch-" + Guid.NewGuid().ToString("N"));
        var workingCopy = Path.Combine(root, "working-copy");
        var copiedWorkingCopy = Path.Combine(root, "copied-working-copy");
        Directory.CreateDirectory(root);
        try {
            var repository = new SvnMemoryRepository();
            await using var server = await TestServer.StartAsync(repository);
            var revision = await repository.GetLatestRevisionAsync();
            revision = await repository.CommitAsync(new(revision, RevisionProperties("trunk"), [
                SvnCommitChange.AddFile(new("trunk/app.txt"), "trunk"u8)
            ]));
            var trunkRevision = revision;
            revision = await repository.CommitAsync(new(revision, RevisionProperties("branch"), [
                SvnCommitChange.Copy(new("branches/test"), SvnNodeKind.Directory, new(new("trunk"), trunkRevision))
            ]));
            revision = await repository.CommitAsync(new(revision, RevisionProperties("branch change"), [
                SvnCommitChange.ModifyFile(new("branches/test/app.txt"), "branch"u8)
            ]));

            await RunSvnAsync(server, "checkout", server.Url + "/trunk", workingCopy);
            await RunSvnAsync(server, "switch", server.Url + "/branches/test", workingCopy);
            Assert.Equal("branch", await File.ReadAllTextAsync(Path.Combine(workingCopy, "app.txt")));

            var diff = await RunSvnAsync(server, "diff", "-r", "1:2", server.Url + "/readme.txt");
            Assert.Contains("-old", diff);
            Assert.Contains("+hello over http", diff);

            await repository.CommitAsync(new(revision, RevisionProperties("copy"), [
                SvnCommitChange.Copy(new("releases/v1"), SvnNodeKind.Directory, new(new("branches/test"), revision))
            ]));
            await RunSvnAsync(server, "checkout", server.Url + "/releases/v1", copiedWorkingCopy);
            Assert.Equal("branch", await File.ReadAllTextAsync(Path.Combine(copiedWorkingCopy, "app.txt")));
        } finally { DeleteDirectory(root); }
    }
    [Fact]
    public async Task OfficialClientCanModifyAndCommit() {
        var root = Path.Combine(Path.GetTempPath(), "svnflux-http-commit-" + Guid.NewGuid().ToString("N"));
        var workingCopy = Path.Combine(root, "working-copy");
        Directory.CreateDirectory(root);
        try {
            var repository = new SvnMemoryRepository();
            await using var server = await TestServer.StartAsync(repository);
            await RunSvnAsync(server, "checkout", server.Url, workingCopy);
            await File.WriteAllTextAsync(Path.Combine(workingCopy, "readme.txt"), "committed over http");

            var commit = await RunSvnAsync(server, "commit", "-m", "http commit", Path.Combine(workingCopy, "readme.txt"));

            Assert.Contains("Committed revision 3.", commit);
            Assert.Equal("committed over http", await RunSvnAsync(server, "cat", server.Url + "/readme.txt"));
            Assert.Contains("http commit", await RunSvnAsync(server, "log", "-r", "3", server.Url));
        } finally { DeleteDirectory(root); }
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfficialClientCanAddDeleteCopySetPropertiesAndCommit(bool fileSystem) {
        var root = Path.Combine(Path.GetTempPath(), "svnflux-http-full-commit-" + Guid.NewGuid().ToString("N"));
        var workingCopy = Path.Combine(root, "working-copy");
        Directory.CreateDirectory(root);
        try {
            ISvnWritableRepository repository = fileSystem
                ? await SvnFileSystemRepository.CreateAsync(Path.Combine(root, "repository"))
                : new SvnMemoryRepository();
            await using var server = await TestServer.StartAsync(repository);
            await RunSvnAsync(server, "checkout", server.Url, workingCopy);

            var notes = Path.Combine(workingCopy, "notes");
            Directory.CreateDirectory(notes);
            await File.WriteAllTextAsync(Path.Combine(notes, "new.txt"), "new over http");
            await RunSvnAsync(server, "add", notes);
            await RunSvnAsync(server, "copy", Path.Combine(workingCopy, "src"), Path.Combine(workingCopy, "copied-src"));
            await File.WriteAllTextAsync(Path.Combine(workingCopy, "copied-src", "code.cs"), "class Copied { int Value; }");
            await RunSvnAsync(server, "move", Path.Combine(workingCopy, "docs.txt"), Path.Combine(workingCopy, "guide.txt"));
            await RunSvnAsync(server, "delete", Path.Combine(workingCopy, "src", "code.cs"));
            await RunSvnAsync(server, "propset", "demo", "v3", Path.Combine(workingCopy, "readme.txt"));

            var commit = await RunSvnAsync(server, "commit", "-m", "full http commit", workingCopy);

            Assert.Contains("Committed revision 3.", commit);
            Assert.Equal("new over http", await RunSvnAsync(server, "cat", server.Url + "/notes/new.txt"));
            Assert.Equal("class Copied { int Value; }", await RunSvnAsync(server, "cat", server.Url + "/copied-src/code.cs"));
            Assert.Equal("documentation", await RunSvnAsync(server, "cat", server.Url + "/guide.txt"));
            Assert.Equal("v3", await RunSvnAsync(server, "propget", "demo", server.Url + "/readme.txt"));
            var committedRoot = await repository.OpenRevisionAsync(new(3));
            var committedProperties = await committedRoot.GetPropertiesAsync(new("readme.txt"));
            Assert.Contains(committedProperties, property => property.Name == "demo" && Encoding.UTF8.GetString(property.Value.Span) == "v3");
            var list = await RunSvnAsync(server, "list", server.Url);
            Assert.DoesNotContain("docs.txt", list);
            Assert.Equal("", await RunSvnAsync(server, "list", server.Url + "/src"));
        } finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task OutOfDateCommitReturnsSvnErrorAndPublishesNothing() {
        var root = Path.Combine(Path.GetTempPath(), "svnflux-http-out-of-date-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(root);
        try {
            var repository = new SvnMemoryRepository();
            await using var server = await TestServer.StartAsync(repository);
            await RunSvnAsync(server, "checkout", server.Url, first);
            await RunSvnAsync(server, "checkout", server.Url, second);
            await File.WriteAllTextAsync(Path.Combine(first, "readme.txt"), "first writer");
            await RunSvnAsync(server, "commit", "-m", "first", Path.Combine(first, "readme.txt"));
            await File.WriteAllTextAsync(Path.Combine(second, "readme.txt"), "stale writer");

            var failure = await Record.ExceptionAsync(() => RunSvnAsync(server, "commit", "-m", "stale", Path.Combine(second, "readme.txt")));

            Assert.NotNull(failure);
            Assert.Contains("E160028", failure.Message);
            Assert.Equal(new SvnRevision(3), await repository.GetLatestRevisionAsync());
            Assert.Equal("first writer", await RunSvnAsync(server, "cat", server.Url + "/readme.txt"));
        } finally { DeleteDirectory(root); }
    }
    [Fact]
    public async Task OfficialClientCanCommitLargeBinaryFile() {
        var root = Path.Combine(Path.GetTempPath(), "svnflux-http-large-commit-" + Guid.NewGuid().ToString("N"));
        var workingCopy = Path.Combine(root, "working-copy");
        Directory.CreateDirectory(root);
        try {
            await using var server = await TestServer.StartAsync();
            await RunSvnAsync(server, "checkout", server.Url, workingCopy);
            var expected = Enumerable.Range(0, 3_000_017).Select(value => (byte)(value * 131 + 17)).ToArray();
            var path = Path.Combine(workingCopy, "large.bin");
            await File.WriteAllBytesAsync(path, expected);
            await RunSvnAsync(server, "add", path);

            var commit = await RunSvnAsync(server, "commit", "-m", "large binary", path);

            Assert.Contains("Committed revision 3.", commit);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            Assert.Equal(expected, await client.GetByteArrayAsync(server.Url + "/large.bin"));
        } finally { DeleteDirectory(root); }
    }

    private static SvnRevisionProperties RevisionProperties(string message) =>
        new("tester", DateTimeOffset.UtcNow, message, SvnPropertyCollection.Empty);

    private static void DeleteDirectory(string path) {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, true);
    }
    private static async Task<string> RunSvnAsync(TestServer server, params string[] arguments) {
        var config = Path.Combine(Path.GetTempPath(), "svnflux-http-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(config);
        try {
            var start = new ProcessStartInfo("svn") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("--non-interactive"); start.ArgumentList.Add("--config-dir"); start.ArgumentList.Add(config);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var stdout = await output; var stderr = await error;
            Assert.True(process.ExitCode == 0, $"svn exited with {process.ExitCode}:\n{stderr}\nHTTP trace:\n{string.Join('\n', server.Trace)}");
            return stdout.Trim();
        } finally { Directory.Delete(config, true); }
    }

    private sealed class TestServer : IAsyncDisposable {
        private readonly WebApplication _application;
        private TestServer(WebApplication application, string url, List<SvnHttpTrace> trace) { _application = application; Url = url; Trace = trace; }
        public string Url { get; }
        public List<SvnHttpTrace> Trace { get; }

        public static Task<TestServer> StartAsync() => StartAsync(new SvnMemoryRepository(Guid.Parse("b7ab8e59-dbd8-4c5b-9c49-f6d8f723981a")));

        public static async Task<TestServer> StartAsync(ISvnWritableRepository repository) {
            var first = await repository.CommitAsync(new(new SvnRevision(0), new("tester", DateTimeOffset.Parse("2026-07-12T00:00:00Z"), "initial", SvnPropertyCollection.Empty), [
                SvnCommitChange.AddFile(new("readme.txt"), "old"u8),
                SvnCommitChange.AddFile(new("src/code.cs"), "class C {}"u8)
            ]));
            await repository.CommitAsync(new(first, new("tester", DateTimeOffset.Parse("2026-07-12T01:00:00Z"), "second", SvnPropertyCollection.Empty), [
                SvnCommitChange.ModifyFile(new("readme.txt"), Encoding.UTF8.GetBytes("hello over http")).WithPropertyChanges([SvnPropertyChange.Set("demo", "v1"u8)]),
                SvnCommitChange.AddFile(new("docs.txt"), "documentation"u8)
            ]));
            return await StartHostAsync(app => app.MapSvnRepository("/svn/repository", repository), "/svn/repository");
        }

        public static Task<TestServer> StartManyAsync(IReadOnlyDictionary<string, ISvnRepository> repositories) =>
            StartHostAsync(app => app.MapSvnRepositories("/svn", (_, name, _) => ValueTask.FromResult(repositories.GetValueOrDefault(name))), "/svn");

        private static async Task<TestServer> StartHostAsync(Action<WebApplication> map, string path) {
            var trace = new List<SvnHttpTrace>();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.AddSvnFluxHttp(options => options.Trace = value => { lock (trace) trace.Add(value); });
            var app = builder.Build(); map(app);
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            return new(app, addresses.Addresses.Single().TrimEnd('/') + path, trace);
        }

        public async ValueTask DisposeAsync() { await _application.StopAsync(); await _application.DisposeAsync(); }
    }
}

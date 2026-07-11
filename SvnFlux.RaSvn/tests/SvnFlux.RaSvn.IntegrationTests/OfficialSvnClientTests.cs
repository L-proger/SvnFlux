using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using SvnFlux.Core;
using SvnFlux.RaSvn.Server;
using SvnFlux.Repository.FileSystem;

namespace SvnFlux.RaSvn.IntegrationTests;

public sealed class OfficialSvnClientTests
{
    [Fact(Timeout = 60_000)]
    public async Task OfficialClientCanCommitAndCheckoutLargeBinaryFile() {
        var testPath = Path.Combine(Path.GetTempPath(), "SvnFlux.Tests", Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(testPath, "repository");
        var workingCopy = Path.Combine(testPath, "working-copy");
        var checkout = Path.Combine(testPath, "checkout");
        var repository = await SvnFileSystemRepository.CreateAsync(repositoryPath);
        await using var server = new SvnServer(new SvnSingleRepositoryResolver("repository", repository), new SvnServerOptions { Port = 0 });
        using var cancellation = new CancellationTokenSource();
        var serverTask = server.RunAsync(cancellation.Token);
        try {
            var endpoint = server.LocalEndpoint ?? throw new InvalidOperationException("The test server did not start listening.");
            var url = $"svn://127.0.0.1:{endpoint.Port}/repository";
            await RunSvnAsync(["checkout", url, workingCopy], cancellation.Token);
            var content = new byte[12 * 1024 * 1024];
            new Random(42).NextBytes(content);
            var filePath = Path.Combine(workingCopy, "large.bin");
            await File.WriteAllBytesAsync(filePath, content, cancellation.Token);
            await RunSvnAsync(["add", "large.bin"], cancellation.Token, workingCopy);
            Assert.Contains("Committed revision 1", await RunSvnAsync(["commit", "-m", "Large binary"], cancellation.Token, workingCopy), StringComparison.Ordinal);

            await RunSvnAsync(["checkout", url, checkout], cancellation.Token);

            await using var checkedOut = File.OpenRead(Path.Combine(checkout, "large.bin"));
            Assert.Equal(SHA256.HashData(content), await SHA256.HashDataAsync(checkedOut, cancellation.Token));
        }
        finally {
            await cancellation.CancelAsync();
            await serverTask;
            DeleteDirectory(testPath);
        }
    }

    [Fact(Timeout = 45_000)]
    public async Task OfficialClientCanUseSwitchRemoteOperationsRevisionPropertiesAndLocks() {
        var testPath = Path.Combine(Path.GetTempPath(), "SvnFlux.Tests", Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(testPath, "repository");
        var workingCopy = Path.Combine(testPath, "working-copy");
        var secondWorkingCopy = Path.Combine(testPath, "working-copy-2");
        var repository = await SvnFileSystemRepository.CreateAsync(repositoryPath);
        await repository.CreateRevisionAsync([
            SvnFileSystemChange.AddDirectory(new SvnRepositoryPath("trunk")),
            SvnFileSystemChange.AddDirectory(new SvnRepositoryPath("branches")),
            SvnFileSystemChange.Write(new SvnRepositoryPath("trunk/main.txt"), "trunk\n"u8)
        ], new SvnRevisionProperties("SvnFlux", DateTimeOffset.UtcNow, "Initial layout", SvnPropertyCollection.Empty));
        var diagnostics = new List<string>();
        await using var server = new SvnServer(new SvnSingleRepositoryResolver("repository", repository), new SvnServerOptions { Port = 0, DiagnosticLog = diagnostics.Add, ProtocolTrace = diagnostics.Add });
        using var cancellation = new CancellationTokenSource();
        var serverTask = server.RunAsync(cancellation.Token);

        try {
            var endpoint = server.LocalEndpoint ?? throw new InvalidOperationException("The test server did not start listening.");
            var url = $"svn://127.0.0.1:{endpoint.Port}/repository";
            Assert.Contains("Committed revision 2", await RunSvnAsync(["copy", url + "/trunk", url + "/branches/feature", "-m", "Create feature branch"], cancellation.Token), StringComparison.Ordinal);
            Assert.Contains("Committed revision 3", await RunSvnAsync(["mkdir", url + "/temporary", "-m", "Create temporary"], cancellation.Token), StringComparison.Ordinal);
            Assert.Contains("Committed revision 4", await RunSvnAsync(["move", url + "/branches/feature", url + "/branches/renamed", "-m", "Rename feature"], cancellation.Token), StringComparison.Ordinal);
            Assert.Contains("Committed revision 5", await RunSvnAsync(["delete", url + "/temporary", "-m", "Delete temporary"], cancellation.Token), StringComparison.Ordinal);
            Assert.Contains("renamed/", await RunSvnAsync(["list", url + "/branches"], cancellation.Token), StringComparison.Ordinal);

            await RunSvnAsync(["checkout", url + "/trunk", workingCopy], cancellation.Token);
            var switchOutput = await RunSvnAsync(["switch", url + "/branches/renamed", workingCopy], cancellation.Token);
            Assert.Contains("revision 5", switchOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(url + "/branches/renamed", await RunSvnAsync(["info"], cancellation.Token, workingCopy), StringComparison.OrdinalIgnoreCase);

            Assert.Equal("Initial layout", (await RunSvnAsync(["propget", "--revprop", "svn:log", "-r", "1", url], cancellation.Token)).Trim());
            await RunSvnAsync(["propset", "--revprop", "svn:log", "Corrected initial layout", "-r", "1", url], cancellation.Token);
            Assert.Equal("Corrected initial layout", (await RunSvnAsync(["propget", "--revprop", "svn:log", "-r", "1", url], cancellation.Token)).Trim());
            await RunSvnAsync(["propset", "--revprop", "custom:reviewed", "yes", "-r", "1", url], cancellation.Token);
            Assert.Equal("yes", (await RunSvnAsync(["propget", "--revprop", "custom:reviewed", "-r", "1", url], cancellation.Token)).Trim());
            await RunSvnAsync(["propdel", "--revprop", "custom:reviewed", "-r", "1", url], cancellation.Token);
            Assert.DoesNotContain("custom:reviewed", await RunSvnAsync(["proplist", "--revprop", "-r", "1", url], cancellation.Token), StringComparison.Ordinal);

            await RunSvnAsync(["propset", "svn:needs-lock", "*", "main.txt"], cancellation.Token, workingCopy);
            await RunSvnAsync(["commit", "-m", "Require lock"], cancellation.Token, workingCopy);
            var lockOutput = await RunSvnAsync(["lock", "main.txt", "-m", "Editing main"], cancellation.Token, workingCopy);
            Assert.Contains("locked by user", lockOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Lock Token:", await RunSvnAsync(["info", "main.txt"], cancellation.Token, workingCopy), StringComparison.Ordinal);
            await RunSvnAsync(["checkout", url + "/branches/renamed", secondWorkingCopy], cancellation.Token);
            File.SetAttributes(Path.Combine(secondWorkingCopy, "main.txt"), FileAttributes.Normal);
            await File.WriteAllTextAsync(Path.Combine(secondWorkingCopy, "main.txt"), "without token\n", new UTF8Encoding(false), cancellation.Token);
            var lockedCommit = await Assert.ThrowsAsync<InvalidOperationException>(async () => await RunSvnAsync(["commit", "-m", "No token"], cancellation.Token, secondWorkingCopy));
            Assert.Contains("lock", lockedCommit.Message, StringComparison.OrdinalIgnoreCase);

            await File.WriteAllTextAsync(Path.Combine(workingCopy, "main.txt"), "with token\n", new UTF8Encoding(false), cancellation.Token);
            Assert.Contains("Committed revision 7", await RunSvnAsync(["commit", "-m", "Commit with lock"], cancellation.Token, workingCopy), StringComparison.Ordinal);
            Assert.DoesNotContain("Lock Token:", await RunSvnAsync(["info", "main.txt"], cancellation.Token, workingCopy), StringComparison.Ordinal);
        }
        catch (Exception exception) { throw new Xunit.Sdk.XunitException($"{exception.Message}{Environment.NewLine}Server diagnostics:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}"); }
        finally {
            await cancellation.CancelAsync();
            await serverTask;
            DeleteDirectory(testPath);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task OfficialClientsAutoMergeAndReportPropertyAndTreeConflicts() {
        var testPath = Path.Combine(Path.GetTempPath(), "SvnFlux.Tests", Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(testPath, "repository");
        var workingCopyA = Path.Combine(testPath, "wc-a");
        var workingCopyB = Path.Combine(testPath, "wc-b");
        var repository = await SvnFileSystemRepository.CreateAsync(repositoryPath);
        await repository.CreateRevisionAsync([
            SvnFileSystemChange.Write(new SvnRepositoryPath("merge.txt"), "first\nmiddle\nlast\n"u8),
            SvnFileSystemChange.Write(new SvnRepositoryPath("props.txt"), "properties\n"u8),
            SvnFileSystemChange.Write(new SvnRepositoryPath("tree.txt"), "tree\n"u8)
        ], new SvnRevisionProperties("SvnFlux", DateTimeOffset.UtcNow, "Conflict fixtures", SvnPropertyCollection.Empty));
        var diagnostics = new List<string>();
        await using var server = new SvnServer(new SvnSingleRepositoryResolver("repository", repository), new SvnServerOptions { Port = 0, DiagnosticLog = diagnostics.Add, ProtocolTrace = diagnostics.Add });
        using var cancellation = new CancellationTokenSource();
        var serverTask = server.RunAsync(cancellation.Token);

        try {
            var endpoint = server.LocalEndpoint ?? throw new InvalidOperationException("The test server did not start listening.");
            var url = $"svn://127.0.0.1:{endpoint.Port}/repository";
            await RunSvnAsync(["checkout", url, workingCopyA], cancellation.Token);
            await RunSvnAsync(["checkout", url, workingCopyB], cancellation.Token);

            await File.WriteAllTextAsync(Path.Combine(workingCopyA, "merge.txt"), "FIRST from A\nmiddle\nlast\n", new UTF8Encoding(false), cancellation.Token);
            await RunSvnAsync(["commit", "-m", "A changes first line"], cancellation.Token, workingCopyA);
            await File.WriteAllTextAsync(Path.Combine(workingCopyB, "merge.txt"), "first\nmiddle\nLAST from B\n", new UTF8Encoding(false), cancellation.Token);
            var mergeUpdate = await RunSvnAsync(["update"], cancellation.Token, workingCopyB);
            Assert.Contains("G    merge.txt", mergeUpdate, StringComparison.Ordinal);
            Assert.Equal("FIRST from A\nmiddle\nLAST from B\n", NormalizeNewlines(await File.ReadAllTextAsync(Path.Combine(workingCopyB, "merge.txt"), cancellation.Token)));
            Assert.Contains("Committed revision 3", await RunSvnAsync(["commit", "-m", "Automatic merge"], cancellation.Token, workingCopyB), StringComparison.Ordinal);

            await RunSvnAsync(["update"], cancellation.Token, workingCopyA);
            await RunSvnAsync(["propset", "custom:color", "red", "props.txt"], cancellation.Token, workingCopyA);
            await RunSvnAsync(["commit", "-m", "Red property"], cancellation.Token, workingCopyA);
            await RunSvnAsync(["propset", "custom:color", "blue", "props.txt"], cancellation.Token, workingCopyB);
            var propertyUpdate = await RunSvnAsync(["update"], cancellation.Token, workingCopyB);
            Assert.Contains(" C   props.txt", propertyUpdate, StringComparison.Ordinal);
            var propertyStatusLine = (await RunSvnAsync(["status"], cancellation.Token, workingCopyB)).Split('\n').Single(line => line.Contains("props.txt", StringComparison.Ordinal) && !line.Contains(".prej", StringComparison.Ordinal));
            Assert.Contains("C", propertyStatusLine, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(workingCopyB, "props.txt.prej")));
            await RunSvnAsync(["resolve", "--accept", "mine-full", "props.txt"], cancellation.Token, workingCopyB);
            Assert.Contains("Committed revision 5", await RunSvnAsync(["commit", "-m", "Resolve blue property"], cancellation.Token, workingCopyB), StringComparison.Ordinal);

            await RunSvnAsync(["update"], cancellation.Token, workingCopyA);
            await RunSvnAsync(["delete", "tree.txt"], cancellation.Token, workingCopyA);
            await RunSvnAsync(["commit", "-m", "Delete tree target"], cancellation.Token, workingCopyA);
            await File.WriteAllTextAsync(Path.Combine(workingCopyB, "tree.txt"), "locally edited tree\n", new UTF8Encoding(false), cancellation.Token);
            var treeUpdate = await RunSvnAsync(["update"], cancellation.Token, workingCopyB);
            Assert.Contains("tree conflict", treeUpdate, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("C", (await RunSvnAsync(["status"], cancellation.Token, workingCopyB)).Split('\n').Single(line => line.Contains("tree.txt", StringComparison.Ordinal)), StringComparison.Ordinal);
        }
        catch (Exception exception) { throw new Xunit.Sdk.XunitException($"{exception.Message}{Environment.NewLine}Server diagnostics:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}"); }
        finally {
            await cancellation.CancelAsync();
            await serverTask;
            DeleteDirectory(testPath);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task OfficialClientsHandleOutOfDateTextConflictResolveAndCommit() {
        var testPath = Path.Combine(Path.GetTempPath(), "SvnFlux.Tests", Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(testPath, "repository");
        var workingCopyA = Path.Combine(testPath, "wc-a");
        var workingCopyB = Path.Combine(testPath, "wc-b");
        var repository = await SvnFileSystemRepository.CreateAsync(repositoryPath);
        await repository.CreateRevisionAsync([SvnFileSystemChange.Write(new SvnRepositoryPath("shared.txt"), "top\noriginal\nbottom\n"u8)], new SvnRevisionProperties("SvnFlux", DateTimeOffset.UtcNow, "Initial shared file", SvnPropertyCollection.Empty));
        var diagnostics = new List<string>();
        await using var server = new SvnServer(new SvnSingleRepositoryResolver("repository", repository), new SvnServerOptions { Port = 0, DiagnosticLog = diagnostics.Add, ProtocolTrace = diagnostics.Add });
        using var cancellation = new CancellationTokenSource();
        var serverTask = server.RunAsync(cancellation.Token);

        try {
            var endpoint = server.LocalEndpoint ?? throw new InvalidOperationException("The test server did not start listening.");
            var url = $"svn://127.0.0.1:{endpoint.Port}/repository";
            await RunSvnAsync(["checkout", url, workingCopyA], cancellation.Token);
            await RunSvnAsync(["checkout", url, workingCopyB], cancellation.Token);
            await File.WriteAllTextAsync(Path.Combine(workingCopyA, "shared.txt"), "top\nfrom A\nbottom\n", new UTF8Encoding(false), cancellation.Token);
            Assert.Contains("Committed revision 2", await RunSvnAsync(["commit", "-m", "Change from A"], cancellation.Token, workingCopyA), StringComparison.Ordinal);

            await File.WriteAllTextAsync(Path.Combine(workingCopyB, "shared.txt"), "top\nfrom B\nbottom\n", new UTF8Encoding(false), cancellation.Token);
            var staleCommit = await Assert.ThrowsAsync<InvalidOperationException>(async () => await RunSvnAsync(["commit", "-m", "Stale B"], cancellation.Token, workingCopyB));
            Assert.Contains("out of date", staleCommit.Message, StringComparison.OrdinalIgnoreCase);

            var update = await RunSvnAsync(["update"], cancellation.Token, workingCopyB);
            Assert.Contains("C    shared.txt", update, StringComparison.Ordinal);
            Assert.Contains("C       shared.txt", await RunSvnAsync(["status"], cancellation.Token, workingCopyB), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(workingCopyB, "shared.txt.mine")));
            Assert.Contains("<<<<<<<", await File.ReadAllTextAsync(Path.Combine(workingCopyB, "shared.txt"), cancellation.Token), StringComparison.Ordinal);

            await RunSvnAsync(["resolve", "--accept", "mine-full", "shared.txt"], cancellation.Token, workingCopyB);
            Assert.DoesNotContain("C       shared.txt", await RunSvnAsync(["status"], cancellation.Token, workingCopyB), StringComparison.Ordinal);
            Assert.Contains("Committed revision 3", await RunSvnAsync(["commit", "-m", "Resolve with B"], cancellation.Token, workingCopyB), StringComparison.Ordinal);
            Assert.Equal("top\nfrom B\nbottom\n", NormalizeNewlines(await RunSvnAsync(["cat", url + "/shared.txt"], cancellation.Token)));
        }
        catch (Exception exception) { throw new Xunit.Sdk.XunitException($"{exception.Message}{Environment.NewLine}Server diagnostics:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}"); }
        finally {
            await cancellation.CancelAsync();
            await serverTask;
            DeleteDirectory(testPath);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task OfficialClientCanBlameAndReadFileHistoryAcrossMove() {
        var testPath = Path.Combine(Path.GetTempPath(), "SvnFlux.Tests", Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(testPath, "repository");
        var repository = await SvnFileSystemRepository.CreateAsync(repositoryPath);
        var path = new SvnRepositoryPath("story.txt");
        var r1 = await repository.CommitAsync(new SvnCommitRequest(new SvnRevision(0), RevisionProperties("Alice", "Create story"), [SvnCommitChange.AddFile(path, "origin\nalpha\nbeta\n"u8)]));
        var r2 = await repository.CommitAsync(new SvnCommitRequest(r1, RevisionProperties("Bob", "Extend story"), [SvnCommitChange.ModifyFile(path, "origin\nalpha\nBETA\ngamma\n"u8)]));
        var moved = new SvnRepositoryPath("renamed.txt");
        var r3 = await repository.CommitAsync(new SvnCommitRequest(r2, RevisionProperties("Carol", "Rename story"), [
            SvnCommitChange.Copy(moved, SvnNodeKind.File, new SvnCopySource(path, r2)),
            SvnCommitChange.Delete(path, SvnNodeKind.File)
        ]));
        await repository.CommitAsync(new SvnCommitRequest(r3, RevisionProperties("Dan", "Polish story"), [SvnCommitChange.ModifyFile(moved, "origin\nALPHA\nBETA\ngamma\n"u8)]));
        var diagnostics = new List<string>();
        await using var server = new SvnServer(new SvnSingleRepositoryResolver("repository", repository), new SvnServerOptions { Port = 0, DiagnosticLog = diagnostics.Add, ProtocolTrace = diagnostics.Add });
        using var cancellation = new CancellationTokenSource();
        var serverTask = server.RunAsync(cancellation.Token);

        try {
            var endpoint = server.LocalEndpoint ?? throw new InvalidOperationException("The test server did not start listening.");
            var url = $"svn://127.0.0.1:{endpoint.Port}/repository";
            var blame = await RunSvnAsync(["blame", url + "/renamed.txt"], cancellation.Token);
            Assert.Contains("Alice", blame, StringComparison.Ordinal);
            Assert.Contains("Bob", blame, StringComparison.Ordinal);
            Assert.Contains("Dan", blame, StringComparison.Ordinal);
            Assert.Equal("origin\nalpha\nBETA\ngamma\n", NormalizeNewlines(await RunSvnAsync(["cat", "-r", "2", url + "/renamed.txt@4"], cancellation.Token)));
            var changeDiff = await RunSvnAsync(["diff", "-c", "4", url + "/renamed.txt"], cancellation.Token);
            Assert.Contains("-alpha", changeDiff, StringComparison.Ordinal);
            Assert.Contains("+ALPHA", changeDiff, StringComparison.Ordinal);
            Assert.Contains(diagnostics, message => message.Contains("file-rev /story.txt@1", StringComparison.Ordinal));
            Assert.Contains(diagnostics, message => message.Contains("file-rev /renamed.txt@4", StringComparison.Ordinal));
        }
        catch (Exception exception) { throw new Xunit.Sdk.XunitException($"{exception.Message}{Environment.NewLine}Server diagnostics:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}"); }
        finally {
            await cancellation.CancelAsync();
            await serverTask;
            DeleteDirectory(testPath);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task OfficialClientCanCommitReadUpdateAndDeleteProperties() {
        var testPath = Path.Combine(Path.GetTempPath(), "SvnFlux.Tests", Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(testPath, "repository");
        var workingCopyPath = Path.Combine(testPath, "working-copy");
        var secondWorkingCopyPath = Path.Combine(testPath, "working-copy-2");
        var repository = await SvnFileSystemRepository.CreateAsync(repositoryPath);
        await repository.CreateRevisionAsync([SvnFileSystemChange.Write(new SvnRepositoryPath("script.txt"), "line\n"u8)], new SvnRevisionProperties("SvnFlux", DateTimeOffset.UtcNow, "Initial properties tree", SvnPropertyCollection.Empty));
        var diagnostics = new List<string>();
        await using var server = new SvnServer(new SvnSingleRepositoryResolver("repository", repository), new SvnServerOptions { Port = 0, DiagnosticLog = diagnostics.Add, ProtocolTrace = diagnostics.Add });
        using var cancellation = new CancellationTokenSource();
        var serverTask = server.RunAsync(cancellation.Token);

        try {
            var endpoint = server.LocalEndpoint ?? throw new InvalidOperationException("The test server did not start listening.");
            var url = $"svn://127.0.0.1:{endpoint.Port}/repository";
            await RunSvnAsync(["checkout", url, workingCopyPath], cancellation.Token);
            await RunSvnAsync(["propset", "svn:ignore", "*.tmp", "."], cancellation.Token, workingCopyPath);
            await RunSvnAsync(["propset", "svn:mime-type", "text/plain", "script.txt"], cancellation.Token, workingCopyPath);
            await RunSvnAsync(["propset", "svn:eol-style", "LF", "script.txt"], cancellation.Token, workingCopyPath);
            await RunSvnAsync(["propset", "svn:executable", "*", "script.txt"], cancellation.Token, workingCopyPath);
            await RunSvnAsync(["propset", "custom:binary", "value", "script.txt"], cancellation.Token, workingCopyPath);
            Assert.Contains("Committed revision 2", await RunSvnAsync(["commit", "-m", "Set properties"], cancellation.Token, workingCopyPath), StringComparison.Ordinal);

            Assert.Equal("*.tmp", NormalizeNewlines(await RunSvnAsync(["propget", "svn:ignore", url], cancellation.Token)).TrimEnd());
            Assert.Equal("text/plain", NormalizeNewlines(await RunSvnAsync(["propget", "svn:mime-type", url + "/script.txt"], cancellation.Token)).TrimEnd());
            Assert.Equal("*", NormalizeNewlines(await RunSvnAsync(["propget", "svn:executable", url + "/script.txt"], cancellation.Token)).TrimEnd());
            Assert.Contains("custom:binary", await RunSvnAsync(["proplist", url + "/script.txt"], cancellation.Token), StringComparison.Ordinal);
            await RunSvnAsync(["checkout", url, secondWorkingCopyPath], cancellation.Token);
            await File.WriteAllTextAsync(Path.Combine(secondWorkingCopyPath, "ignored.tmp"), "ignored", cancellation.Token);
            Assert.DoesNotContain("ignored.tmp", await RunSvnAsync(["status"], cancellation.Token, secondWorkingCopyPath), StringComparison.Ordinal);

            await RunSvnAsync(["propdel", "custom:binary", "script.txt"], cancellation.Token, workingCopyPath);
            Assert.Contains("Committed revision 3", await RunSvnAsync(["commit", "-m", "Delete property"], cancellation.Token, workingCopyPath), StringComparison.Ordinal);
            Assert.DoesNotContain("custom:binary", await RunSvnAsync(["proplist", url + "/script.txt"], cancellation.Token), StringComparison.Ordinal);
            Assert.Contains("revision 3", await RunSvnAsync(["update"], cancellation.Token, secondWorkingCopyPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) { throw new Xunit.Sdk.XunitException($"{exception.Message}{Environment.NewLine}Server diagnostics:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}"); }
        finally {
            await cancellation.CancelAsync();
            await serverTask;
            DeleteDirectory(testPath);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task OfficialClientCanCopyMoveDiffAndStatus() {
        var testPath = Path.Combine(Path.GetTempPath(), "SvnFlux.Tests", Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(testPath, "repository");
        var workingCopyPath = Path.Combine(testPath, "working-copy");
        var repository = await SvnFileSystemRepository.CreateAsync(repositoryPath);
        await repository.CreateRevisionAsync([
            SvnFileSystemChange.Write(new SvnRepositoryPath("source.txt"), "source body\n"u8),
            SvnFileSystemChange.Write(new SvnRepositoryPath("folder/nested.txt"), "nested body\n"u8)
        ], new SvnRevisionProperties("SvnFlux", DateTimeOffset.UtcNow, "Initial copy tree", SvnPropertyCollection.Empty));
        var diagnostics = new List<string>();
        await using var server = new SvnServer(new SvnSingleRepositoryResolver("repository", repository), new SvnServerOptions { Port = 0, DiagnosticLog = diagnostics.Add, ProtocolTrace = diagnostics.Add });
        using var cancellation = new CancellationTokenSource();
        var serverTask = server.RunAsync(cancellation.Token);

        try {
            var endpoint = server.LocalEndpoint ?? throw new InvalidOperationException("The test server did not start listening.");
            var url = $"svn://127.0.0.1:{endpoint.Port}/repository";
            await RunSvnAsync(["checkout", url, workingCopyPath], cancellation.Token);
            await RunSvnAsync(["copy", "source.txt", "copied.txt"], cancellation.Token, workingCopyPath);
            await RunSvnAsync(["copy", "folder", "folder-copy"], cancellation.Token, workingCopyPath);
            await RunSvnAsync(["move", "source.txt", "moved.txt"], cancellation.Token, workingCopyPath);

            var status = await RunSvnAsync(["status"], cancellation.Token, workingCopyPath);
            Assert.Contains("A  +    copied.txt", status, StringComparison.Ordinal);
            Assert.Contains("D       source.txt", status, StringComparison.Ordinal);
            Assert.Contains("A  +    moved.txt", status, StringComparison.Ordinal);
            var localDiff = await RunSvnAsync(["diff", "--notice-ancestry"], cancellation.Token, workingCopyPath);
            Assert.Contains("copied.txt", localDiff, StringComparison.Ordinal);

            Assert.Contains("Committed revision 2", await RunSvnAsync(["commit", "-m", "Copy and move"], cancellation.Token, workingCopyPath), StringComparison.Ordinal);
            Assert.Equal("source body\n", NormalizeNewlines(await RunSvnAsync(["cat", url + "/copied.txt"], cancellation.Token)));
            Assert.Equal("source body\n", NormalizeNewlines(await RunSvnAsync(["cat", url + "/moved.txt"], cancellation.Token)));
            Assert.Equal("nested body\n", NormalizeNewlines(await RunSvnAsync(["cat", url + "/folder-copy/nested.txt"], cancellation.Token)));
            var log = await RunSvnAsync(["log", "-v", "-r", "2", url], cancellation.Token);
            Assert.Contains("A /copied.txt (from /source.txt:1)", log, StringComparison.Ordinal);
            Assert.Contains("A /moved.txt (from /source.txt:1)", log, StringComparison.Ordinal);

            var remoteDiff = await RunSvnAsync(["diff", "--notice-ancestry", "-r", "1:2", url], cancellation.Token);
            Assert.Contains("copied.txt", remoteDiff, StringComparison.Ordinal);
            Assert.Contains("moved.txt", remoteDiff, StringComparison.Ordinal);
            Assert.Contains("Status against revision", await RunSvnAsync(["status", "-u"], cancellation.Token, workingCopyPath), StringComparison.Ordinal);
        }
        catch (Exception exception) { throw new Xunit.Sdk.XunitException($"{exception.Message}{Environment.NewLine}Server diagnostics:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}"); }
        finally {
            await cancellation.CancelAsync();
            await serverTask;
            DeleteDirectory(testPath);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task OfficialClientCanModifyAddDeleteAndCommit() {
        var testPath = Path.Combine(Path.GetTempPath(), "SvnFlux.Tests", Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(testPath, "repository");
        var workingCopyPath = Path.Combine(testPath, "working-copy");
        var repository = await SvnFileSystemRepository.CreateAsync(repositoryPath);
        await repository.CreateRevisionAsync([
            SvnFileSystemChange.Write(new SvnRepositoryPath("modify.txt"), "before\n"u8),
            SvnFileSystemChange.Write(new SvnRepositoryPath("delete.txt"), "delete me\n"u8)
        ], new SvnRevisionProperties("SvnFlux", DateTimeOffset.UtcNow, "Initial commit tree", SvnPropertyCollection.Empty));

        var diagnostics = new List<string>();
        await using var server = new SvnServer(new SvnSingleRepositoryResolver("repository", repository), new SvnServerOptions {
            Port = 0,
            DiagnosticLog = message => diagnostics.Add(message),
            ProtocolTrace = message => diagnostics.Add(message)
        });
        using var cancellation = new CancellationTokenSource();
        var serverTask = server.RunAsync(cancellation.Token);

        try {
            var endpoint = server.LocalEndpoint ?? throw new InvalidOperationException("The test server did not start listening.");
            var repositoryUrl = $"svn://127.0.0.1:{endpoint.Port}/repository";
            await RunSvnAsync(["checkout", repositoryUrl, workingCopyPath], cancellation.Token);
            await File.WriteAllTextAsync(Path.Combine(workingCopyPath, "modify.txt"), "after\n", new UTF8Encoding(false), cancellation.Token);
            await File.WriteAllTextAsync(Path.Combine(workingCopyPath, "added.txt"), "new file\n", new UTF8Encoding(false), cancellation.Token);
            Directory.CreateDirectory(Path.Combine(workingCopyPath, "empty-dir"));
            await RunSvnAsync(["add", "added.txt", "empty-dir"], cancellation.Token, workingCopyPath);
            await RunSvnAsync(["delete", "delete.txt"], cancellation.Token, workingCopyPath);

            var commitOutput = await RunSvnAsync(["commit", "-m", "Modify add delete"], cancellation.Token, workingCopyPath);

            Assert.Contains("Committed revision 2", commitOutput, StringComparison.Ordinal);
            Assert.Equal("after\n", NormalizeNewlines(await RunSvnAsync(["cat", repositoryUrl + "/modify.txt"], cancellation.Token)));
            Assert.Equal("new file\n", NormalizeNewlines(await RunSvnAsync(["cat", repositoryUrl + "/added.txt"], cancellation.Token)));
            var list = await RunSvnAsync(["list", repositoryUrl], cancellation.Token);
            Assert.Contains("empty-dir/", list, StringComparison.Ordinal);
            Assert.DoesNotContain("delete.txt", list, StringComparison.Ordinal);
            Assert.Contains("Modify add delete", await RunSvnAsync(["log", "-r", "2", repositoryUrl], cancellation.Token), StringComparison.Ordinal);
            Assert.Contains("At revision 2", await RunSvnAsync(["update"], cancellation.Token, workingCopyPath), StringComparison.Ordinal);
        }
        catch (Exception exception) {
            throw new Xunit.Sdk.XunitException($"{exception.Message}{Environment.NewLine}Server diagnostics:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}");
        }
        finally {
            await cancellation.CancelAsync();
            await serverTask;
            DeleteDirectory(testPath);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task OfficialClientCanCheckoutAndUpdateFilesystemRepository()
    {
        var testPath = Path.Combine(Path.GetTempPath(), "SvnFlux.Tests", Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(testPath, "repository");
        var workingCopyPath = Path.Combine(testPath, "working-copy");
        var repository = await SvnFileSystemRepository.CreateAsync(repositoryPath);
        await repository.CreateRevisionAsync(
            [
                SvnFileSystemChange.Write(new SvnRepositoryPath("readme.txt"), "revision one\n"u8),
                SvnFileSystemChange.Write(new SvnRepositoryPath("old.txt"), "remove me\n"u8),
                SvnFileSystemChange.Write(new SvnRepositoryPath("existing/keep.txt"), "initial nested file\n"u8)
            ],
            new SvnRevisionProperties("SvnFlux", DateTimeOffset.UtcNow, "Initial tree", SvnPropertyCollection.Empty));

        var diagnostics = new List<string>();
        await using var server = new SvnServer(
            new SvnSingleRepositoryResolver("repository", repository),
            new SvnServerOptions
            {
                Port = 0,
                DiagnosticLog = message => diagnostics.Add(message)
            });
        using var cancellation = new CancellationTokenSource();
        var serverTask = server.RunAsync(cancellation.Token);

        try
        {
            var endpoint = server.LocalEndpoint ?? throw new InvalidOperationException("The test server did not start listening.");
            var repositoryUrl = $"svn://127.0.0.1:{endpoint.Port}/repository";
            await RunSvnAsync(["checkout", repositoryUrl, workingCopyPath], cancellation.Token);

            Assert.Equal("revision one\n", NormalizeNewlines(await File.ReadAllTextAsync(Path.Combine(workingCopyPath, "readme.txt"), cancellation.Token)));
            Assert.Equal("remove me\n", NormalizeNewlines(await File.ReadAllTextAsync(Path.Combine(workingCopyPath, "old.txt"), cancellation.Token)));

            var binaryContent = Enumerable.Range(0, 150_000).Select(value => (byte)value).ToArray();
            await repository.CreateRevisionAsync(
                [
                    SvnFileSystemChange.Write(new SvnRepositoryPath("readme.txt"), "revision two\n"u8),
                    SvnFileSystemChange.Delete(new SvnRepositoryPath("old.txt")),
                    SvnFileSystemChange.Write(new SvnRepositoryPath("existing/keep.txt"), "updated nested file\n"u8),
                    SvnFileSystemChange.Write(new SvnRepositoryPath("existing/new.txt"), "new nested file\n"u8),
                    SvnFileSystemChange.Write(new SvnRepositoryPath("nested/data.bin"), binaryContent)
                ],
                new SvnRevisionProperties("SvnFlux", DateTimeOffset.UtcNow, "Update tree", SvnPropertyCollection.Empty));

            var updateOutput = await RunSvnAsync(["update"], cancellation.Token, workingCopyPath);

            Assert.Contains("Updated to revision 2", updateOutput, StringComparison.Ordinal);
            Assert.Equal("revision two\n", NormalizeNewlines(await File.ReadAllTextAsync(Path.Combine(workingCopyPath, "readme.txt"), cancellation.Token)));
            Assert.False(File.Exists(Path.Combine(workingCopyPath, "old.txt")));
            Assert.Equal("updated nested file\n", NormalizeNewlines(await File.ReadAllTextAsync(Path.Combine(workingCopyPath, "existing", "keep.txt"), cancellation.Token)));
            Assert.Equal("new nested file\n", NormalizeNewlines(await File.ReadAllTextAsync(Path.Combine(workingCopyPath, "existing", "new.txt"), cancellation.Token)));
            Assert.Equal(binaryContent, await File.ReadAllBytesAsync(Path.Combine(workingCopyPath, "nested", "data.bin"), cancellation.Token));
            Assert.Contains(diagnostics, message => message.Contains("svndiff1", StringComparison.Ordinal));
        }
        catch (Exception exception)
        {
            throw new Xunit.Sdk.XunitException($"{exception.Message}{Environment.NewLine}Server diagnostics:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}");
        }
        finally
        {
            await cancellation.CancelAsync();
            await serverTask;
            DeleteDirectory(testPath);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task OfficialClientObservesManualMutationOfSharedFileBody()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), "SvnFlux.Tests", Guid.NewGuid().ToString("N"));
        var repository = await SvnFileSystemRepository.CreateAsync(repositoryPath);
        await repository.CreateRevisionAsync(
            [SvnFileSystemChange.Write(new SvnRepositoryPath("readme.txt"), "original\n"u8)],
            new SvnRevisionProperties("SvnFlux", DateTimeOffset.UtcNow, "Add readme", SvnPropertyCollection.Empty));
        await repository.CreateRevisionAsync(
            [SvnFileSystemChange.Write(new SvnRepositoryPath("marker.txt"), "revision two\n"u8)],
            new SvnRevisionProperties("SvnFlux", DateTimeOffset.UtcNow, "Add marker", SvnPropertyCollection.Empty));

        await using var server = new SvnServer(
            new SvnSingleRepositoryResolver("repository", repository),
            new SvnServerOptions { Port = 0 });
        using var cancellation = new CancellationTokenSource();
        var serverTask = server.RunAsync(cancellation.Token);

        try
        {
            var endpoint = server.LocalEndpoint ?? throw new InvalidOperationException("The test server did not start listening.");
            var repositoryUrl = $"svn://127.0.0.1:{endpoint.Port}/repository";
            var fileUrl = $"svn://127.0.0.1:{endpoint.Port}/repository/readme.txt";
            Assert.Contains("Revision: 2", await RunSvnAsync(["info", repositoryUrl], cancellation.Token), StringComparison.Ordinal);
            Assert.Contains("readme.txt", await RunSvnAsync(["list", repositoryUrl], cancellation.Token), StringComparison.Ordinal);
            var log = await RunSvnAsync(["log", "-v", repositoryUrl], cancellation.Token);
            Assert.Contains("r2 | SvnFlux", log, StringComparison.Ordinal);
            Assert.Contains("r1 | SvnFlux", log, StringComparison.Ordinal);
            Assert.Equal("original\n", NormalizeNewlines(await RunSvnAsync(["cat", fileUrl + "@1"], cancellation.Token)));
            Assert.Equal("original\n", NormalizeNewlines(await RunSvnAsync(["cat", fileUrl + "@2"], cancellation.Token)));

            var revisionTwoFile = Path.Combine(repositoryPath, "revisions", "000002", "tree", "readme.txt");
            await File.WriteAllTextAsync(revisionTwoFile, "manually changed\n", new UTF8Encoding(false), cancellation.Token);

            Assert.Equal("manually changed\n", NormalizeNewlines(await RunSvnAsync(["cat", fileUrl + "@1"], cancellation.Token)));
            Assert.Equal("manually changed\n", NormalizeNewlines(await RunSvnAsync(["cat", fileUrl + "@2"], cancellation.Token)));
        }
        finally
        {
            await cancellation.CancelAsync();
            await serverTask;
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task OfficialClientCanInspectReadAndLogRepository()
    {
        var diagnostics = new List<string>();
        var repository = new TestRepository();
        await using var server = new SvnServer(
            new SvnSingleRepositoryResolver("repository", repository),
            new SvnServerOptions
            {
                Port = 0,
                DiagnosticLog = message => diagnostics.Add(message)
            });
        using var cancellation = new CancellationTokenSource();
        var serverTask = server.RunAsync(cancellation.Token);

        try
        {
            var endpoint = server.LocalEndpoint ?? throw new InvalidOperationException("The test server did not start listening.");
            var url = $"svn://127.0.0.1:{endpoint.Port}/repository";
            var info = await RunSvnAsync(["info", url], cancellation.Token);
            var list = await RunSvnAsync(["list", url], cancellation.Token);
            var cat = await RunSvnAsync(["cat", $"{url}/readme.txt"], cancellation.Token);
            var log = await RunSvnAsync(["log", "-v", url], cancellation.Token);

            Assert.Contains("Revision: 1", info, StringComparison.Ordinal);
            Assert.Contains("readme.txt", list, StringComparison.Ordinal);
            Assert.Equal("Hello from SvnFlux.\n", cat.Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.Contains("r1 | SvnFlux", log, StringComparison.Ordinal);
            Assert.Contains("A /readme.txt", log, StringComparison.Ordinal);
            Assert.Contains("Add readme.txt", log, StringComparison.Ordinal);
        }
        catch (Exception exception)
        {
            throw new Xunit.Sdk.XunitException($"{exception.Message}{Environment.NewLine}Server diagnostics:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}");
        }
        finally
        {
            await cancellation.CancelAsync();
            await serverTask;
        }
    }

    private static async Task<string> RunSvnAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        var configDirectory = Path.Combine(Path.GetTempPath(), "SvnFlux.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDirectory);
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("svn")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
                }
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.StartInfo.ArgumentList.Add("--non-interactive");
            process.StartInfo.ArgumentList.Add("--config-dir");
            process.StartInfo.ArgumentList.Add(configDirectory);
            if (!process.Start())
            {
                throw new InvalidOperationException("The official svn client could not be started.");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"svn {string.Join(' ', arguments)} did not finish within 10 seconds.");
            }

            var output = await standardOutput;
            var error = await standardError;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"svn {string.Join(' ', arguments)} exited with code {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{output}{Environment.NewLine}stderr:{Environment.NewLine}{error}");
            }

            return output;
        }
        finally
        {
            Directory.Delete(configDirectory, recursive: true);
        }
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
    private static SvnRevisionProperties RevisionProperties(string author, string message) => new(author, DateTimeOffset.UtcNow, message, SvnPropertyCollection.Empty);

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private sealed class TestRepository : ISvnRepository
    {
        private static readonly byte[] Content = Encoding.UTF8.GetBytes("Hello from SvnFlux.\n");
        private static readonly DateTimeOffset Date = new(2026, 7, 10, 12, 1, 0, TimeSpan.Zero);
        private static readonly SvnRevision RevisionOne = new(1);
        private static readonly SvnRevisionProperties Properties = new("SvnFlux", Date, "Add readme.txt", SvnPropertyCollection.Empty);

        public Guid Id { get; } = Guid.Parse("49bb2b3d-f659-4c39-83df-1e952a1f2cf2");

        public ValueTask<SvnRevision> GetLatestRevisionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(RevisionOne);

        public ValueTask<ISvnRevisionRoot> OpenRevisionAsync(SvnRevision revision, CancellationToken cancellationToken = default) =>
            revision.Value is 0 or 1
                ? ValueTask.FromResult<ISvnRevisionRoot>(new TestRevisionRoot(revision, revision.Value == 1))
                : ValueTask.FromException<ISvnRevisionRoot>(new SvnInvalidRevisionException(revision));

        public ValueTask<SvnRevisionProperties> GetRevisionPropertiesAsync(SvnRevision revision, CancellationToken cancellationToken = default) =>
            revision.Value switch
            {
                0 => ValueTask.FromResult(SvnRevisionProperties.Empty),
                1 => ValueTask.FromResult(Properties),
                _ => ValueTask.FromException<SvnRevisionProperties>(new SvnInvalidRevisionException(revision))
            };

        public async IAsyncEnumerable<SvnLogEntry> GetLogAsync(
            SvnLogQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new SvnLogEntry(
                RevisionOne,
                Properties,
                [new SvnChangedPath(new SvnRepositoryPath("readme.txt"), SvnChangeAction.Add, SvnNodeKind.File, true, false)]);
        }

        private sealed class TestRevisionRoot(SvnRevision revision, bool containsFile) : ISvnRevisionRoot
        {
            public SvnRevision Revision { get; } = revision;

            public ValueTask<SvnNodeInfo?> GetNodeInfoAsync(SvnRepositoryPath path, CancellationToken cancellationToken = default)
            {
                if (path.IsRoot)
                {
                    return ValueTask.FromResult<SvnNodeInfo?>(new SvnNodeInfo(SvnNodeKind.Directory, 0, false, Revision, Date, Revision.Value == 1 ? "SvnFlux" : null));
                }

                return ValueTask.FromResult<SvnNodeInfo?>(containsFile && path.Value == "readme.txt" ? FileInfo() : null);
            }

            public ValueTask<Stream> OpenFileAsync(SvnRepositoryPath path, CancellationToken cancellationToken = default) =>
                containsFile && path.Value == "readme.txt"
                    ? ValueTask.FromResult<Stream>(new MemoryStream(Content, writable: false))
                    : ValueTask.FromException<Stream>(new SvnPathNotFoundException(path));

            public async IAsyncEnumerable<SvnDirectoryEntry> GetDirectoryAsync(
                SvnRepositoryPath path,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                if (containsFile && path.IsRoot)
                {
                    yield return new SvnDirectoryEntry("readme.txt", FileInfo());
                }
            }

            public ValueTask<SvnPropertyCollection> GetPropertiesAsync(SvnRepositoryPath path, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(SvnPropertyCollection.Empty);

            private static SvnNodeInfo FileInfo() => new(
                SvnNodeKind.File,
                Content.Length,
                false,
                RevisionOne,
                Date,
                "SvnFlux",
                new SvnChecksum(SvnChecksumAlgorithm.Md5, MD5.HashData(Content)));
        }
    }
}

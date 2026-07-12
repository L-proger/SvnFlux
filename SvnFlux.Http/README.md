# SvnFlux.Http

SvnFlux.Http exposes an ASP.NET Core endpoint for modern Subversion HTTPv2 clients. Repositories stay transport-independent and are supplied through `ISvnRepository` or `ISvnWritableRepository`.

## Registration

```csharp
builder.Services.AddSvnFluxHttp(options => {
    options.HookError = exception => logger.LogError(exception, "An SVN post-hook failed.");
});

app.MapSvnRepository("/svn/project", repository);
```

Use `MapSvnRepositories` when one endpoint resolves multiple repositories.

## Merge tracking

The server advertises mergeinfo support and implements the `svn:mergeinfo-report` REPORT, including explicit, inherited, nearest-ancestor, and descendant queries. Merge history remains the normal versioned `svn:mergeinfo` node property.

The official SVN client performs the three-way merge locally. SvnFlux supplies copy ancestry, logs, revision trees, deltas, properties, and mergeinfo; the resulting file and property changes are committed through the normal HTTP transaction editor.

## Replay and mirroring

The `svn:replay-report` endpoint streams one revision as standard editor XML. HTTPv2 revision resources, partial `include-path` replay, copy ancestry, property changes, deletions, svndiff0/svndiff1, and bounded file windows are supported.

`replay-range` requires no separate HTTP document: modern clients request the individual revision resources in sequence. The compatibility suite runs the official `svnsync` executable between two SvnFlux HTTP repositories and verifies revision properties, copies, deleted paths, and large binary contents.

## Inherited properties

The advertised `svn:inherited-props` capability returns ancestor properties in root-to-parent order through `svn:inherited-props-report`. Text values use XML text and unsafe binary values use base64. This supports commands such as:

    svn proplist --show-inherited-props --verbose URL/path

## Hooks

Register one or more `ISvnHttpHook` implementations with dependency injection:

```csharp
builder.Services.AddSingleton<ISvnHttpHook, RepositoryPolicy>();

sealed class RepositoryPolicy : ISvnHttpHook {
    public ValueTask BeforeCommitAsync(SvnHttpCommitHookContext context, CancellationToken token = default) {
        if (context.Request.RevisionProperties.LogMessage is null)
            throw new SvnHttpHookRejectedException("A log message is required.");
        return ValueTask.CompletedTask;
    }

    public ValueTask AfterCommitAsync(SvnHttpCommitHookContext context, CancellationToken token = default) {
        Console.WriteLine($"Committed r{context.CommittedRevision}");
        return ValueTask.CompletedTask;
    }
}
```

Available callbacks are `BeforeCommitAsync`, `AfterCommitAsync`, `BeforeLockAsync`, and `AfterLockAsync`.

A pre-hook may reject an operation by throwing `SvnHttpHookRejectedException`; nothing is published or locked. Post-hooks run only after the repository operation succeeds. Their exceptions are sent to `SvnHttpOptions.HookError` and do not turn an already successful commit or lock into a protocol failure.

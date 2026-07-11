# SvnFlux.RaSvn

Managed .NET 10 implementation of the native Subversion `svn://` protocol.

Run the interactive filesystem-backed scenario through the sibling `SvnFlux.Playground` CLI project:

```powershell
dotnet run --project ..\SvnFlux.Playground -- rasvn --port 3690 --repository repository
```

Pass `--trace` to view decoded client/server editor traffic and svndiff instructions.

Verify it with an official client:

```powershell
svn info svn://127.0.0.1:3690/repository
svn list svn://127.0.0.1:3690/repository
svn cat svn://127.0.0.1:3690/repository/readme.txt
svn log -v svn://127.0.0.1:3690/repository
```

Checkout/update, `status -u`, URL/revision diff, and commits with modifications, additions, deletions, copies, moves, and file/directory properties are supported. This includes `propset`, `propget`, `proplist`, `propdel`, `svn:ignore`, `svn:mime-type`, `svn:eol-style`, `svn:executable`, and arbitrary binary property values. Copy ancestry is reported by `svn log -v`. Authentication, locks, and switch remain future work.

File history is available through `svn blame`, `svn cat -r REV URL@PEG`, and `svn diff -c REV URL`. History follows copy/move ancestry and transmits compact svndiff0/1 deltas between consecutive file revisions.

Multiple working copies can commit concurrently. Stale nodes receive a normal SVN out-of-date error; update performs client-side three-way merging and produces the standard text, property, and tree conflict state used by `svn status` and `svn resolve`.

Working copies can switch between copied branches with `svn switch`. Repository-only `svn mkdir`, `copy`, `move`, and `delete` are handled through the same commit editor without requiring checkout.

Revision properties support `propget/proplist/propset/propdel --revprop`, including atomic expected-value checks. Persistent file locks support `svn lock`, `svn unlock`, batch commands, lock-token enforcement during commit, `--no-unlock`, and `svn:needs-lock` working-copy behavior.

See `SvnFlux.Core/docs/ra-svn-protocol.md` in the sibling repository for implemented behavior and limitations.

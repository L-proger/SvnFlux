# ra_svn protocol

## Current compatibility milestone

The first SvnFlux vertical slice implements protocol version 2 over TCP with:

- server greeting and capability negotiation;
- unauthenticated read-only sessions;
- repository UUID and root URL exchange;
- explicit `Handshake`, `Authentication`, and `MainCommands` states;
- typed decoding and validation before repository access;
- repository routing through `ISvnRepositoryResolver`;
- `get-latest-rev`, `check-path`, `stat`, `get-dir`, `get-file`, `get-file-revs`, `get-locations`, `get-location-segments`, `log`, `get-lock`, `get-locks`, `lock`, `lock-many`, `unlock`, `unlock-many`, `rev-prop`, `rev-proplist`, `change-rev-prop`, `change-rev-prop2`, `reparent`, `update`, `switch`, `status`, and `diff`;
- streamed file content and log entries backed by `ISvnRepository`.
- root working-copy reports through `set-path`, `finish-report`, and `abort-report`;
- server-driven update editors for directory/file addition, modification, deletion, properties, and text deltas;
- negotiated svndiff version 0/1 windows with source-copy, target-copy, new-data instructions, and conditional zlib compression.

The implementation is covered by integration tests that launch the server on a dynamic port and invoke the official Subversion command-line client. `svn info`, `svn list`, `svn cat`, `svn log -v`, `svn checkout`, `svn update`, `svn status -u`, URL revision diffs, copy, move, and commit complete successfully against filesystem repository data.

The Playground scenario opens a persistent `SvnFlux.Repository.FileSystem` repository. Its `--publish-update` option creates one new revision before the server starts so a previously checked-out working copy can exercise `svn update`.

The commit editor accepts modify-only commits, file and directory additions (including empty directories), deletes, copy-from ancestry, and binary property deltas on files, directories, and the repository root. Move is represented by the standard SVN copy-plus-delete pair. Property set/delete operations can be combined atomically with content and copy changes. Incoming svndiff0/svndiff1 streams are applied to the repository base or copy source and checked against the client's MD5 checksums. The final commit is published atomically through `ISvnWritableRepository`; stale base revisions fail as out-of-date.

Mixed-revision reports produced by the official client immediately after a commit are supported for full-depth working copies. Status and diff share the report/editor engine with update; ordinary working-copy `svn status` and `svn diff` remain client-local, while `status -u` and URL/revision diffs exercise the server commands. Properties are returned by `get-file`/`get-dir` and carried by checkout/update editors, including property deletions. Authenticated sessions, locks, and switch remain unsupported. Unsupported main commands return an ra_svn command failure.

The server advertises `edit-pipeline`, `svndiff1`, and `depth`. It selects svndiff1 only when the client advertises the same capability and otherwise falls back to version 0.

File history follows copy ancestry backwards from the requested peg path. The server then emits chronological file-rev records containing revision properties, binary node-property deltas, and an svndiff stream against the preceding historical contents. This supports `svn blame` across copy/move, `svn cat -r N URL@PEG`, and change diffs for individual files. `get-locations` resolves historical paths across copy ancestry. Reverse file-rev delivery is deliberately not advertised yet.

Concurrent working copies are supported through node-level optimistic concurrency. A commit reports SVN out-of-date before accepting a delta when the reported node revision is older than the repository node. The server drains already-pipelined editor commands after an early failure so the session remains protocol-correct. Mixed-revision update reports may identify a different base revision for every path; deltas and checksums are generated against each reported revision root. This enables automatic non-overlapping text merges, standard text conflict artifacts, property conflicts with `.prej`, and tree-conflict discovery. `get-location-segments` supplies the ancestry ranges requested by modern clients while explaining tree conflicts.

Switch uses distinct base and target repository anchors while retaining the same working-copy-relative editor paths. URL-to-URL mkdir/copy/move/delete use the normal commit editor, so they share atomic publication, ancestry, properties, and out-of-date behavior with working-copy commits.

The server advertises `commit-revprops` and `atomic-revprops`. Revision properties support individual/list reads, set/delete, and compare-and-swap through `change-rev-prop2`. The filesystem backend atomically replaces the authoritative revprops sidecar and rejects a stale expected value.

Locks are persistent filesystem records containing path, opaque token, owner, comment, creation time, and optional expiration. Single and batch lock commands are supported. A commit touching a locked path must carry the matching token; successful commits release their supplied locks unless `keep-locks` is requested. `svn:needs-lock` remains an ordinary versioned node property and therefore makes unlocked working-copy files read-only through normal client behavior.

`SvnServerOptions.ProtocolTrace` exposes a human-readable protocol trace. The Playground `rasvn --trace` viewer includes editor commands plus each svndiff window and its `Source`, `Target`, and `NewData` instructions.

## Running the server

From the `SvnFlux.Playground` repository:

```powershell
dotnet run -- rasvn --port 3690 --repository repository
```

Then, from another terminal:

```powershell
svn info svn://127.0.0.1:3690/repository
svn list svn://127.0.0.1:3690/repository
svn cat svn://127.0.0.1:3690/repository/readme.txt
svn log -v svn://127.0.0.1:3690/repository
svn checkout svn://127.0.0.1:3690/repository working-copy
svn update working-copy
```

The arguments are the TCP port and repository name. The listener binds to loopback by default.

## Design boundaries

Wire strings remain binary until a typed protocol field explicitly requires UTF-8 text. The reader enforces configurable limits for string length, word length, list nesting, and list item count. TCP sessions are isolated and bounded by a configurable concurrency limit.

Protocol messages are decoded into typed commands before accessing repository contracts. Repository URLs are resolved through `ISvnRepositoryResolver`; the protocol layer does not own repository content. Observations from other compatible servers may inform interoperability tests, but implementation behavior is checked against the official specification.

## References

- [Apache Subversion ra_svn protocol](https://svn.apache.org/repos/asf/subversion/trunk/subversion/libsvn_ra_svn/protocol)
- [Apache Subversion ra_svn client implementation](https://github.com/apache/subversion/blob/trunk/subversion/libsvn_ra_svn/client.c)
- [Apache Subversion svnserve command implementation](https://github.com/apache/subversion/blob/trunk/subversion/svnserve/serve.c)
- [git-as-svn](https://github.com/git-as-svn/git-as-svn), consulted for interoperability scope only; its GPL-2.0 code is not copied or mechanically translated

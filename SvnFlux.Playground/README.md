# SvnFlux.Playground

.NET 10 command-line application for manually exercising SvnFlux components.

Each global feature is represented by a separate scenario file under `Scenarios/` and exposed as a `System.CommandLine` subcommand.

Run the current ra_svn scenario:

```powershell
dotnet run -- rasvn --port 3690 --repository repository --path ..\.playground-data\rasvn-repository
```

Add `--trace` to print the ra_svn conversation and decoded svndiff windows/instructions. In VS Code the same mode is available as the `Playground: RaSvn trace viewer` F5 configuration.

Then connect an official Subversion client from another terminal:

```powershell
svn info svn://127.0.0.1:3690/repository
svn list svn://127.0.0.1:3690/repository
svn cat svn://127.0.0.1:3690/repository/readme.txt
svn log -v svn://127.0.0.1:3690/repository
svn blame svn://127.0.0.1:3690/repository/readme.txt
svn cat -r 1 svn://127.0.0.1:3690/repository/readme.txt@2
```

The server also accepts ordinary working-copy commits containing file modifications, file/directory additions (including empty directories), and deletes. For example, after checkout use `svn add`, `svn delete`, and `svn commit -m "Test commit"`; the files are published as the next filesystem revision.

On its first run the scenario creates a persistent filesystem repository. Revision 1 owns the ordinary `readme.txt` body; revision 2 adds `revision-2.txt` and reuses the unchanged readme body through a hard link. Later runs reopen the same repository.

You can edit either of these files while the server is running:

```text
.playground-data/rasvn-repository/revisions/000001/tree/readme.txt
.playground-data/rasvn-repository/revisions/000002/tree/readme.txt
```

The edit is immediately visible through both `svn cat svn://localhost:3690/repository/readme.txt@1` and the corresponding `@2` command; no new revision is created. The current RaSvn milestone does not yet implement the `get-file-revs` command used by the alternative `svn cat -r N` spelling.

To exercise checkout and update from VS Code:

1. Start `Playground: RaSvn` with F5.
2. Run `svn checkout svn://127.0.0.1:3690/repository working-copy`.
3. Stop the server and start `Playground: RaSvn + publish update` with F5. It creates one new revision and starts the server again.
4. Run `svn update` inside `working-copy`.

Every launch of the second configuration publishes another revision which modifies `readme.txt` and adds a revision marker under `updates/`.

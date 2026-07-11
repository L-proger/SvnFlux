# Filesystem repository format

## Intent

`SvnFlux.Repository.FileSystem` is a trusted mutable-history backend. Its primary goal is to expose every revision as a normal, human-readable filesystem tree. It deliberately does not emulate FSFS, use a database, enforce immutable history, or protect repository storage from direct user edits.

The repository owner is responsible for all direct filesystem changes.

## Layout

```text
repository/
├── format.json
├── uuid
├── current
├── revisions/
│   ├── 000000/
│   │   ├── tree/
│   │   ├── manifest.json
│   │   ├── changes.json
│   │   └── metadata.json
│   └── 000001/
│       ├── tree/
│       │   ├── readme.txt
│       │   └── bin/
│       │       └── application.exe
│       ├── manifest.json
│       ├── changes.json
│       └── metadata.json
├── revprops/
├── transactions/
├── locks/
└── journal/
```

Files under `tree/` retain their ordinary names and extensions. Users can read text, play media, execute programs, and edit files with normal operating-system tools.

## Shared file bodies

A file first added or changed by a normal commit is stored as an ordinary file in that revision tree. A later revision that does not change the file contains a hard link to the same filesystem body.

Hard links have no link chain. Metadata still records the revision that first created the current body so a changed commit can replace it with a new ordinary body.

Directly editing a linked file is allowed. The edit changes the shared body and is therefore visible in every revision that references the body. No new revision is created. No copy-on-write or immutable object layer is used.

`.lnk` shell shortcuts and symbolic links are not used. The initial implementation uses filesystem hard links. On Windows this avoids the elevation or Developer Mode requirement of symbolic links. All revision trees must reside on the same filesystem volume; a commit fails if the host filesystem cannot create a hard link. There is deliberately no copying fallback because that would silently break shared-body mutation semantics and duplicate bytes.

## Read behavior

File size, content, and content checksum are read from the live filesystem body. Stored hashes are not used as authoritative content identities. Fresh `svn cat` and checkout operations therefore observe bytes changed manually after publication.

Revision metadata, properties, copy ancestry, and changed-path records remain in manifests and sidecar files. Editing a file body does not automatically rewrite commit author, date, log message, or changed-path metadata.

Node properties are stored as name/base64-value entries in each revision manifest, preserving arbitrary binary values without separate database files. Property-only revisions reuse all file bodies through hard links. Deletion removes the property entry from the new manifest while older manifests retain it.

A copied node records its copy-from repository path and revision. Its file bodies are hard-linked directly from the source body's revision/path, so file and directory copies do not duplicate unchanged bytes. A move is stored as one copied subtree plus deletion of the original subtree.

Revision properties are authoritative in `revprops/NNNNNN.json` and are replaced atomically for post-commit edits. Initial `metadata.json` inside the revision remains the publication-time snapshot.
Each published revision also contains `revprops.json`, the recovery copy used if a process stops between moving the revision and publishing its authoritative revision-property sidecar.

Locks are stored as human-readable JSON sidecars under `locks/`, mirroring repository paths and adding a `.json` suffix. They contain opaque tokens and descriptive metadata but no file contents or database state.

Existing SVN working copies may not discover a manual content change when the repository HEAD revision number remains unchanged. This compatibility limitation is intentional for this backend.

## Physical names

Normal logical repository names are materialized unchanged. A path that cannot be represented safely or unambiguously on the host filesystem may be rejected by this backend. Path traversal is always rejected before filesystem access.

## Commit publication

Normal SVN commits still publish a new numbered revision atomically:

1. Create a temporary revision directory.
2. Write ordinary bodies for added and changed files.
3. Create hard links for unchanged files.
4. Write manifests, changed paths, and metadata.
5. Write `journal/pending.json` with the transaction name and intended revision.
6. Atomically rename the temporary revision directory into `revisions/`.
7. Atomically publish its revision properties.
8. Atomically update `current` and remove the journal record.

After publication, the revision tree remains directly mutable by repository users.

## Recovery and writers

Every mutating operation takes an exclusive OS file handle on `write.lock`. The handle uses `FileShare.None`, so it coordinates independent processes as well as repository objects within one process. A competing writer receives `SvnRepositoryBusyException`; readers remain available.

Opening a repository performs recovery while holding the same lock. A journal record whose revision directory is already present is completed by restoring revision properties and advancing `current`. A journal record whose transaction was never moved is rolled back. Other abandoned transaction directories are removed. Finally, every revision from zero through `current` must have a published directory.

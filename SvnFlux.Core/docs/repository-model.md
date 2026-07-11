# Repository model

`SvnFlux.Core` owns transport-independent repository concepts. Protocol and storage projects depend on these contracts; Core does not depend on either implementation.

## Paths

`SvnRepositoryPath` stores canonical repository-relative paths. The empty string represents the repository root. One leading and one trailing slash are normalized away. Empty segments, `.` and `..` segments, NUL, and backslash characters are rejected. Unicode and case are preserved.

URL decoding belongs to the transport boundary. Filesystem path normalization must never be used for repository paths.

## Read-only contracts

`ISvnRepository` exposes the youngest revision, immutable revision roots, revision properties, and streamed log entries. `ISvnRevisionRoot` exposes node metadata, file streams, streamed directory entries, and binary-valued node properties.

Committed roots are immutable. File content remains binary. Checksums identify their algorithm explicitly.

Expected failures cross the abstraction as typed `SvnRepositoryException` subclasses rather than storage-specific exceptions.

## Writable repositories

`ISvnWritableRepository` extends the read contract with one atomic `CommitAsync` operation. A `SvnCommitRequest` identifies the expected base revision, revision properties, and typed add/modify/delete changes. The filesystem implementation rejects stale bases with `SvnOutOfDateException` and publishes the complete next revision only after all changes have been prepared.

This intentionally small contract currently models complete file contents rather than exposing a long-lived transaction object. RaSvn owns the client-driven editor and converts its completed svndiff streams into these storage-neutral changes.

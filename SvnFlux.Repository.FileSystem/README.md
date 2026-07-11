# SvnFlux.Repository.FileSystem

Mutable, directly browsable filesystem repository backend for SvnFlux.

Each revision is stored under `revisions/<number>/tree` with ordinary file names. New or changed files receive a new physical body. Unchanged files are represented by hard links to the same body, so they consume no duplicate file data.

Direct edits are intentionally allowed. Writing through any revision changes every revision sharing that file body, without creating a new SVN revision. This backend is designed for trusted storage that behaves like a network share, not for immutable archival history.

Changed file bodies are copied into transaction trees with asynchronous streams. Repository mutations are serialized across processes through `write.lock`, and `journal/pending.json` lets repository open finish or roll back a commit interrupted during publication.

See [the filesystem format documentation](../SvnFlux.Core/docs/filesystem-format.md) for the on-disk layout and limitations.

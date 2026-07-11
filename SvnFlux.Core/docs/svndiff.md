# svndiff

## Implemented formats

`SvnFlux.Svndiff` owns the binary delta model, encoding, and decoding used by protocol projects. RaSvn does not duplicate svndiff byte handling.

The implementation supports versions 0 and 1 with a shared window model:

- source-copy instructions reuse bytes from the corresponding source view;
- target-copy instructions reuse bytes already present in the target view;
- new-data instructions carry genuinely new bytes;
- a greedy indexed matcher chooses useful copies and coalesces literal runs;
- RaSvn divides large files into independent 64 KB windows;
- arbitrary binary data and empty files are supported.

Version 1 encodes instruction and new-data sections independently. Each section is compressed with zlib only when the compressed payload is smaller; otherwise its raw representation is retained. This avoids expanding already compressed or high-entropy content.

`SvnDiffDecoder` provides both an in-memory convenience API and a streaming decoder/applier for versions 0 and 1. The streaming API reads one window at a time and writes each completed target view immediately, so memory is bounded by the configured maximum window/section size rather than total file size. Its source stream is currently required to be seekable because svndiff source views carry absolute offsets.

RaSvn update, commit, and file-history paths use 64 KB windows. Incoming commit chunks are spooled to a temporary file, checksummed as streams, applied into a temporary content file, and copied into the repository transaction without materializing the complete file as a `byte[]`.

## RaSvn negotiation

RaSvn advertises `svndiff1` and reads the client capability list during the protocol handshake. Version 1 is used only when both peers advertise it; other clients receive version 0 using the same delta builder.

## Remaining work

- stronger content-defined matching across shifted 64 KB window boundaries;
- additional malformed-window limits and fuzzing;
- optional version 2 LZ4 support.

## Reference

The byte format follows the official Apache Subversion [`notes/svndiff`](https://svn.apache.org/repos/asf/subversion/trunk/notes/svndiff) description.

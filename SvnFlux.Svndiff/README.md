# SvnFlux.Svndiff

Binary svndiff encoding and decoding primitives for SvnFlux.

The library implements version 0 and version 1 encoders, a bounded decoder/applier, a source/target matcher, and source-copy, target-copy, and new-data instructions. Version 1 conditionally compresses instruction and new-data sections with zlib. RaSvn negotiates it through the standard `svndiff1` capability.

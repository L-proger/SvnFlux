namespace SvnFlux.Svndiff;

public sealed record SvnDiffInspection(SvnDiffVersion Version, IReadOnlyList<SvnDiffWindowInspection> Windows);

public sealed record SvnDiffWindowInspection(
    int SourceOffset,
    int SourceLength,
    int TargetLength,
    int WireLength,
    int NewDataLength,
    IReadOnlyList<SvnDiffInstruction> Instructions);

public static class SvnDiffInspector {
    public static SvnDiffInspection Inspect(ReadOnlySpan<byte> delta) {
        if (delta.Length < 4 || !delta[..3].SequenceEqual("SVN"u8)) { throw new InvalidDataException("The svndiff header is invalid or truncated."); }

        var version = delta[3] switch {
            0 => SvnDiffVersion.Zero,
            1 => SvnDiffVersion.One,
            _ => throw new NotSupportedException($"SvnDiff version {delta[3]} is not supported.")
        };
        var windows = new List<SvnDiffWindowInspection>();
        var offset = 4;
        while (offset < delta.Length) {
            var windowStart = offset;
            var sourceOffset = SvnDiffDecoder.ReadLength(delta, ref offset, "source view offset");
            var sourceLength = SvnDiffDecoder.ReadLength(delta, ref offset, "source view length");
            var targetLength = SvnDiffDecoder.ReadLength(delta, ref offset, "target view length");
            var instructionsLength = SvnDiffDecoder.ReadLength(delta, ref offset, "instructions length");
            var newDataLength = SvnDiffDecoder.ReadLength(delta, ref offset, "new data length");
            var instructions = SvnDiffDecoder.ReadSection(delta, ref offset, instructionsLength, version);
            var newData = SvnDiffDecoder.ReadSection(delta, ref offset, newDataLength, version);
            windows.Add(new SvnDiffWindowInspection(sourceOffset, sourceLength, targetLength, offset - windowStart, newData.Length, DecodeInstructions(instructions)));
        }

        return new SvnDiffInspection(version, windows);
    }

    private static IReadOnlyList<SvnDiffInstruction> DecodeInstructions(ReadOnlySpan<byte> encoded) {
        var result = new List<SvnDiffInstruction>();
        var offset = 0;
        var newDataOffset = 0;
        while (offset < encoded.Length) {
            var first = encoded[offset++];
            var selector = first >> 6;
            if (selector == 3) { throw new InvalidDataException("The svndiff instruction selector is invalid."); }
            var length = first & 0x3f;
            if (length == 0) { length = SvnDiffDecoder.ReadLength(encoded, ref offset, "instruction length"); }
            var kind = (SvnDiffInstructionKind)selector;
            var copyOffset = kind == SvnDiffInstructionKind.NewData
                ? newDataOffset
                : SvnDiffDecoder.ReadLength(encoded, ref offset, "copy offset");
            result.Add(new SvnDiffInstruction(kind, copyOffset, length));
            if (kind == SvnDiffInstructionKind.NewData) { newDataOffset += length; }
        }

        return result;
    }
}

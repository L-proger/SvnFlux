using System.Buffers;
using System.IO.Compression;

namespace SvnFlux.Svndiff;

public static class SvnDiffEncoder
{
    public static byte[] EncodeHeader(SvnDiffVersion version)
    {
        ValidateVersion(version);
        return [(byte)'S', (byte)'V', (byte)'N', (byte)version];
    }

    public static byte[] Encode(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> target,
        SvnDiffVersion version)
    {
        var header = EncodeHeader(version);
        if (target.IsEmpty)
        {
            return header;
        }

        var window = EncodeWindow(0, source, target, version);
        var result = new byte[header.Length + window.Length];
        header.CopyTo(result, 0);
        window.CopyTo(result, header.Length);
        return result;
    }

    public static byte[] EncodeWindow(
        long sourceViewOffset,
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> target,
        SvnDiffVersion version)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceViewOffset);
        var window = SvnDiffWindowBuilder.Build(source, target);
        return EncodeWindow(sourceViewOffset, window, version);
    }

    internal static byte[] EncodeWindow(
        long sourceViewOffset,
        SvnDiffWindow window,
        SvnDiffVersion version)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceViewOffset);
        ValidateVersion(version);
        var instructions = EncodeInstructions(window.Instructions);
        var newData = window.NewData.ToArray();
        var encodedInstructions = version == SvnDiffVersion.One ? EncodeVersionOneSection(instructions) : instructions;
        var encodedNewData = version == SvnDiffVersion.One ? EncodeVersionOneSection(newData) : newData;
        var writer = new ArrayBufferWriter<byte>(
            32 + encodedInstructions.Length + encodedNewData.Length);

        SvnDiffInteger.Write(writer, sourceViewOffset);
        SvnDiffInteger.Write(writer, window.SourceViewLength);
        SvnDiffInteger.Write(writer, window.TargetViewLength);
        SvnDiffInteger.Write(writer, encodedInstructions.Length);
        SvnDiffInteger.Write(writer, encodedNewData.Length);
        writer.Write(encodedInstructions);
        writer.Write(encodedNewData);
        return writer.WrittenSpan.ToArray();
    }

    private static byte[] EncodeInstructions(IReadOnlyList<SvnDiffInstruction> instructions)
    {
        var writer = new ArrayBufferWriter<byte>();
        foreach (var instruction in instructions)
        {
            var selector = instruction.Kind switch
            {
                SvnDiffInstructionKind.Source => 0x00,
                SvnDiffInstructionKind.Target => 0x40,
                SvnDiffInstructionKind.NewData => 0x80,
                _ => throw new ArgumentOutOfRangeException(nameof(instruction))
            };
            if (instruction.Length <= 63)
            {
                WriteByte(writer, (byte)(selector | instruction.Length));
            }
            else
            {
                WriteByte(writer, (byte)selector);
                SvnDiffInteger.Write(writer, instruction.Length);
            }

            if (instruction.Kind is SvnDiffInstructionKind.Source or SvnDiffInstructionKind.Target)
            {
                SvnDiffInteger.Write(writer, instruction.Offset);
            }
        }

        return writer.WrittenSpan.ToArray();
    }

    private static byte[] EncodeVersionOneSection(ReadOnlySpan<byte> value)
    {
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(value);
            }

            compressed = output.ToArray();
        }

        var payload = compressed.Length < value.Length ? compressed.AsSpan() : value;
        var writer = new ArrayBufferWriter<byte>(SvnDiffInteger.GetEncodedLength(value.Length) + payload.Length);
        SvnDiffInteger.Write(writer, value.Length);
        writer.Write(payload);
        return writer.WrittenSpan.ToArray();
    }

    private static void WriteByte(IBufferWriter<byte> writer, byte value)
    {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
    }

    private static void ValidateVersion(SvnDiffVersion version)
    {
        if (version is not SvnDiffVersion.Zero and not SvnDiffVersion.One)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "Only svndiff versions 0 and 1 are supported.");
        }
    }
}

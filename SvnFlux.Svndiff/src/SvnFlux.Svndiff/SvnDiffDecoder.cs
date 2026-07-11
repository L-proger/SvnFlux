using System.IO.Compression;

namespace SvnFlux.Svndiff;

public static class SvnDiffDecoder
{
    private const int MaximumSectionLength = 128 * 1024 * 1024;

    public static byte[] Apply(ReadOnlySpan<byte> source, ReadOnlySpan<byte> delta)
    {
        if (delta.Length < 4 || !delta[..3].SequenceEqual("SVN"u8))
        {
            throw new InvalidDataException("The svndiff header is invalid or truncated.");
        }

        var version = delta[3] switch
        {
            0 => SvnDiffVersion.Zero,
            1 => SvnDiffVersion.One,
            _ => throw new NotSupportedException($"SvnDiff version {delta[3]} is not supported.")
        };
        var offset = 4;
        using var target = new MemoryStream();
        while (offset < delta.Length)
        {
            var sourceViewOffset = ReadLength(delta, ref offset, "source view offset");
            var sourceViewLength = ReadLength(delta, ref offset, "source view length");
            var targetViewLength = ReadLength(delta, ref offset, "target view length");
            var instructionsLength = ReadLength(delta, ref offset, "instructions length");
            var newDataLength = ReadLength(delta, ref offset, "new data length");
            if (sourceViewOffset > source.Length - sourceViewLength)
            {
                throw new InvalidDataException("The svndiff source view is outside the source stream.");
            }

            var instructions = ReadSection(delta, ref offset, instructionsLength, version);
            var newData = ReadSection(delta, ref offset, newDataLength, version);
            var targetView = ApplyWindow(
                source.Slice(sourceViewOffset, sourceViewLength),
                targetViewLength,
                instructions,
                newData);
            target.Write(targetView);
        }

        return target.ToArray();
    }

    public static async ValueTask ApplyAsync(Stream source, Stream delta, Stream target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(target);
        if (!source.CanRead || !source.CanSeek) { throw new ArgumentException("The source stream must be readable and seekable.", nameof(source)); }
        if (!delta.CanRead) { throw new ArgumentException("The delta stream must be readable.", nameof(delta)); }
        if (!target.CanWrite) { throw new ArgumentException("The target stream must be writable.", nameof(target)); }

        var header = new byte[4];
        await delta.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 3).SequenceEqual("SVN"u8)) { throw new InvalidDataException("The svndiff header is invalid."); }
        var version = header[3] switch {
            0 => SvnDiffVersion.Zero,
            1 => SvnDiffVersion.One,
            _ => throw new NotSupportedException($"SvnDiff version {header[3]} is not supported.")
        };

        while (await TryReadLengthAsync(delta, "source view offset", cancellationToken).ConfigureAwait(false) is { } sourceOffset) {
            var sourceLength = await ReadLengthAsync(delta, "source view length", cancellationToken).ConfigureAwait(false);
            var targetLength = await ReadLengthAsync(delta, "target view length", cancellationToken).ConfigureAwait(false);
            var instructionsLength = await ReadLengthAsync(delta, "instructions length", cancellationToken).ConfigureAwait(false);
            var newDataLength = await ReadLengthAsync(delta, "new data length", cancellationToken).ConfigureAwait(false);
            if (sourceOffset > source.Length - sourceLength) { throw new InvalidDataException("The svndiff source view is outside the source stream."); }

            var instructions = await ReadSectionAsync(delta, instructionsLength, version, cancellationToken).ConfigureAwait(false);
            var newData = await ReadSectionAsync(delta, newDataLength, version, cancellationToken).ConfigureAwait(false);
            var sourceView = new byte[sourceLength];
            source.Position = sourceOffset;
            await source.ReadExactlyAsync(sourceView, cancellationToken).ConfigureAwait(false);
            var targetView = ApplyWindow(sourceView, targetLength, instructions, newData);
            await target.WriteAsync(targetView, cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] ApplyWindow(
        ReadOnlySpan<byte> sourceView,
        int targetViewLength,
        ReadOnlySpan<byte> instructions,
        ReadOnlySpan<byte> newData)
    {
        var targetView = new byte[targetViewLength];
        var instructionOffset = 0;
        var targetOffset = 0;
        var newDataOffset = 0;
        while (instructionOffset < instructions.Length)
        {
            var first = instructions[instructionOffset++];
            var selector = first >> 6;
            var length = first & 0x3f;
            if (selector == 3)
            {
                throw new InvalidDataException("The svndiff instruction selector is invalid.");
            }

            if (length == 0)
            {
                length = ReadLength(instructions, ref instructionOffset, "instruction length");
            }

            if (length > targetView.Length - targetOffset)
            {
                throw new InvalidDataException("A svndiff instruction exceeds the target view.");
            }

            if (selector == 2)
            {
                if (length > newData.Length - newDataOffset)
                {
                    throw new InvalidDataException("A svndiff new-data instruction exceeds its section.");
                }

                newData.Slice(newDataOffset, length).CopyTo(targetView.AsSpan(targetOffset));
                newDataOffset += length;
            }
            else
            {
                var copyOffset = ReadLength(instructions, ref instructionOffset, "copy offset");
                if (selector == 0)
                {
                    if (copyOffset > sourceView.Length - length)
                    {
                        throw new InvalidDataException("A svndiff source-copy instruction exceeds the source view.");
                    }

                    sourceView.Slice(copyOffset, length).CopyTo(targetView.AsSpan(targetOffset));
                }
                else
                {
                    if (copyOffset >= targetOffset)
                    {
                        throw new InvalidDataException("A svndiff target-copy instruction starts beyond produced target data.");
                    }

                    for (var index = 0; index < length; index++)
                    {
                        targetView[targetOffset + index] = targetView[copyOffset + index];
                    }
                }
            }

            targetOffset += length;
        }

        if (targetOffset != targetView.Length || newDataOffset != newData.Length)
        {
            throw new InvalidDataException("The svndiff window does not consume its target or new-data section exactly.");
        }

        return targetView;
    }

    internal static ReadOnlySpan<byte> ReadSection(
        ReadOnlySpan<byte> delta,
        ref int offset,
        int encodedLength,
        SvnDiffVersion version)
    {
        if (encodedLength > delta.Length - offset)
        {
            throw new InvalidDataException("A svndiff section is truncated.");
        }

        var encoded = delta.Slice(offset, encodedLength);
        offset += encodedLength;
        if (version == SvnDiffVersion.Zero)
        {
            return encoded;
        }

        var sectionOffset = 0;
        var originalLength = ReadLength(encoded, ref sectionOffset, "original section length");
        var payload = encoded[sectionOffset..];
        if (payload.Length == originalLength)
        {
            return payload;
        }

        using var input = new MemoryStream(payload.ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var result = new byte[originalLength];
        zlib.ReadExactly(result);
        if (zlib.ReadByte() != -1)
        {
            throw new InvalidDataException("A compressed svndiff section expands beyond its declared length.");
        }

        return result;
    }

    internal static int ReadLength(ReadOnlySpan<byte> value, ref int offset, string fieldName)
    {
        var result = SvnDiffInteger.Read(value, ref offset);
        if (result > MaximumSectionLength)
        {
            throw new InvalidDataException($"The svndiff {fieldName} exceeds the configured limit.");
        }

        return (int)result;
    }

    private static async ValueTask<byte[]> ReadSectionAsync(Stream delta, int encodedLength, SvnDiffVersion version, CancellationToken cancellationToken)
    {
        var encoded = new byte[encodedLength];
        await delta.ReadExactlyAsync(encoded, cancellationToken).ConfigureAwait(false);
        if (version == SvnDiffVersion.Zero) { return encoded; }
        var offset = 0;
        var originalLength = ReadLength(encoded, ref offset, "original section length");
        if (encoded.Length - offset == originalLength) { return encoded[offset..]; }
        using var input = new MemoryStream(encoded, offset, encoded.Length - offset, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var result = new byte[originalLength];
        await zlib.ReadExactlyAsync(result, cancellationToken).ConfigureAwait(false);
        if (zlib.ReadByte() != -1) { throw new InvalidDataException("A compressed svndiff section expands beyond its declared length."); }
        return result;
    }

    private static async ValueTask<int?> TryReadLengthAsync(Stream stream, string fieldName, CancellationToken cancellationToken)
    {
        var first = new byte[1];
        if (await stream.ReadAsync(first, cancellationToken).ConfigureAwait(false) == 0) { return null; }
        return await ReadLengthAsync(stream, fieldName, cancellationToken, first[0]).ConfigureAwait(false);
    }

    private static ValueTask<int> ReadLengthAsync(Stream stream, string fieldName, CancellationToken cancellationToken) => ReadLengthAsync(stream, fieldName, cancellationToken, null);

    private static async ValueTask<int> ReadLengthAsync(Stream stream, string fieldName, CancellationToken cancellationToken, byte? first)
    {
        long result = 0;
        var value = first ?? await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
        while (true) {
            if (result > (MaximumSectionLength >> 7)) { throw new InvalidDataException($"The svndiff {fieldName} exceeds the configured limit."); }
            result = (result << 7) | (byte)(value & 0x7f);
            if ((value & 0x80) == 0) { break; }
            value = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        if (result > MaximumSectionLength) { throw new InvalidDataException($"The svndiff {fieldName} exceeds the configured limit."); }
        return (int)result;
    }

    private static async ValueTask<byte> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer[0];
    }
}

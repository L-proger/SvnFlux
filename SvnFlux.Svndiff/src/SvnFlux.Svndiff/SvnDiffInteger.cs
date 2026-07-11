using System.Buffers;

namespace SvnFlux.Svndiff;

internal static class SvnDiffInteger
{
    public static void Write(IBufferWriter<byte> writer, long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Span<byte> encoded = stackalloc byte[10];
        var index = encoded.Length;
        do
        {
            encoded[--index] = (byte)(value & 0x7f);
            value >>= 7;
        }
        while (value != 0);

        for (var position = index; position < encoded.Length - 1; position++)
        {
            encoded[position] |= 0x80;
        }

        writer.Write(encoded[index..]);
    }

    public static long Read(ReadOnlySpan<byte> value, ref int offset)
    {
        long result = 0;
        for (var count = 0; count < 10; count++)
        {
            if ((uint)offset >= (uint)value.Length)
            {
                throw new InvalidDataException("The svndiff integer is truncated.");
            }

            var current = value[offset++];
            if (result > (long.MaxValue >> 7))
            {
                throw new InvalidDataException("The svndiff integer is too large.");
            }

            result = (result << 7) | (uint)(current & 0x7f);
            if ((current & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidDataException("The svndiff integer is too large.");
    }

    public static int GetEncodedLength(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        var length = 1;
        while ((value >>= 7) != 0)
        {
            length++;
        }

        return length;
    }
}

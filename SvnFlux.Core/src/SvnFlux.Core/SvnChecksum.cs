using System.Buffers;
using System.Globalization;

namespace SvnFlux.Core;

public enum SvnChecksumAlgorithm
{
    Md5,
    Sha1,
    Sha256
}

public readonly record struct SvnChecksum
{
    private readonly byte[]? _value;

    public SvnChecksum(SvnChecksumAlgorithm algorithm, ReadOnlySpan<byte> value)
    {
        var expectedLength = algorithm switch
        {
            SvnChecksumAlgorithm.Md5 => 16,
            SvnChecksumAlgorithm.Sha1 => 20,
            SvnChecksumAlgorithm.Sha256 => 32,
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
        };

        if (value.Length != expectedLength)
        {
            throw new ArgumentException($"A {algorithm} checksum must contain {expectedLength} bytes.", nameof(value));
        }

        Algorithm = algorithm;
        _value = value.ToArray();
    }

    public SvnChecksumAlgorithm Algorithm { get; }
    public ReadOnlyMemory<byte> Value => _value ?? ReadOnlyMemory<byte>.Empty;

    public string ToHexString()
    {
        var value = Value.Span;
        var characters = ArrayPool<char>.Shared.Rent(value.Length * 2);
        try
        {
            for (var index = 0; index < value.Length; index++)
            {
                value[index].TryFormat(characters.AsSpan(index * 2, 2), out _, "x2", CultureInfo.InvariantCulture);
            }

            return new string(characters, 0, value.Length * 2);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(characters);
        }
    }
}

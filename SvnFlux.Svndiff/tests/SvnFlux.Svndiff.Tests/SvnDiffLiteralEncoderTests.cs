namespace SvnFlux.Svndiff.Tests;

public sealed class SvnDiffLiteralEncoderTests
{
    [Fact]
    public void EncodesSmallLiteralWindow()
    {
        var result = SvnDiffLiteralEncoder.Encode("abc"u8);

        Assert.Equal(
            new byte[]
            {
                (byte)'S', (byte)'V', (byte)'N', 0,
                0, 0, 3, 1, 3,
                0x83,
                (byte)'a', (byte)'b', (byte)'c'
            },
            result);
    }

    [Fact]
    public void EncodesEmptyTargetAsHeaderOnly()
    {
        Assert.Equal(new byte[] { (byte)'S', (byte)'V', (byte)'N', 0 }, SvnDiffLiteralEncoder.Encode([]));
    }

    [Fact]
    public void EncodesLargeLiteralLengthAsVariableInteger()
    {
        var data = Enumerable.Repeat((byte)'x', 130).ToArray();

        var result = SvnDiffLiteralEncoder.EncodeWindow(data);

        Assert.Equal(new byte[] { 0, 0, 0x81, 0x02, 3, 0x81, 0x02, 0x80, 0x81, 0x02 }, result[..10]);
        Assert.Equal(data, result[10..]);
    }
}

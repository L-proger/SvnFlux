using System.Text;

namespace SvnFlux.Svndiff.Tests;

public sealed class SvnDiffEncoderTests
{
    [Theory]
    [InlineData(SvnDiffVersion.Zero)]
    [InlineData(SvnDiffVersion.One)]
    public void RoundTripsSourceTargetAndNewDataInstructions(SvnDiffVersion version)
    {
        var source = Encoding.UTF8.GetBytes("prefix: alpha beta gamma; suffix");
        var target = Encoding.UTF8.GetBytes("prefix: alpha BETA gamma; suffix suffix");

        var encoded = SvnDiffEncoder.Encode(source, target, version);
        var decoded = SvnDiffDecoder.Apply(source, encoded);

        Assert.Equal(target, decoded);
    }

    [Fact]
    public void SmallEditUsesSourceCopiesAndIsMuchSmallerThanLiteralData()
    {
        var source = Enumerable.Range(0, 60_000).Select(index => (byte)(index * 31)).ToArray();
        var target = source.ToArray();
        "small replacement"u8.CopyTo(target.AsSpan(25_000));

        var encoded = SvnDiffEncoder.Encode(source, target, SvnDiffVersion.One);

        Assert.Equal(target, SvnDiffDecoder.Apply(source, encoded));
        Assert.True(encoded.Length < target.Length / 20, $"Expected a compact delta, got {encoded.Length} bytes.");
    }

    [Fact]
    public void VersionOneCompressesHighlyRepetitiveNewData()
    {
        var target = Enumerable.Repeat((byte)'x', 40_000).ToArray();

        var versionZero = SvnDiffEncoder.Encode([], target, SvnDiffVersion.Zero);
        var versionOne = SvnDiffEncoder.Encode([], target, SvnDiffVersion.One);

        Assert.Equal(target, SvnDiffDecoder.Apply([], versionOne));
        Assert.True(versionOne.Length < versionZero.Length);
    }

    [Fact]
    public void BuilderUsesTargetCopiesForRepeatedTargetData()
    {
        var target = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("repeatable-block-", 100)));

        var window = SvnDiffWindowBuilder.Build([], target);

        Assert.Contains(window.Instructions, instruction => instruction.Kind == SvnDiffInstructionKind.Target);
        Assert.True(window.NewData.Length < target.Length);
    }

    [Fact]
    public void VersionOneKeepsIncompressibleSectionsRawInsteadOfInflatingThem()
    {
        var target = new byte[20_000];
        new Random(42).NextBytes(target);

        var versionZero = SvnDiffEncoder.Encode([], target, SvnDiffVersion.Zero);
        var versionOne = SvnDiffEncoder.Encode([], target, SvnDiffVersion.One);

        Assert.Equal(target, SvnDiffDecoder.Apply([], versionOne));
        Assert.True(versionOne.Length <= versionZero.Length + 5);
    }

    [Fact]
    public void MultipleWindowsRoundTripAgainstCorrespondingSourceViews()
    {
        var source = Enumerable.Range(0, 140_000).Select(index => (byte)(index * 17)).ToArray();
        var target = source.ToArray();
        target.AsSpan(70_000, 100).Fill(0xaa);
        var result = new List<byte>(SvnDiffEncoder.EncodeHeader(SvnDiffVersion.One));
        const int windowSize = 64 * 1024;
        for (var offset = 0; offset < target.Length; offset += windowSize)
        {
            var length = Math.Min(windowSize, target.Length - offset);
            result.AddRange(SvnDiffEncoder.EncodeWindow(
                offset,
                source.AsSpan(offset, length),
                target.AsSpan(offset, length),
                SvnDiffVersion.One));
        }

        Assert.Equal(target, SvnDiffDecoder.Apply(source, result.ToArray()));
    }

    [Fact]
    public async Task StreamingDecoderAppliesLargeMultiWindowDelta()
    {
        const int windowSize = 64 * 1024;
        var source = new byte[16 * 1024 * 1024];
        new Random(42).NextBytes(source);
        var target = source.ToArray();
        target.AsSpan(3_000_000, 80_000).Fill(0x5a);
        await using var delta = new MemoryStream();
        await delta.WriteAsync(SvnDiffEncoder.EncodeHeader(SvnDiffVersion.One));
        for (var offset = 0; offset < target.Length; offset += windowSize)
        {
            var length = Math.Min(windowSize, target.Length - offset);
            await delta.WriteAsync(SvnDiffEncoder.EncodeWindow(offset, source.AsSpan(offset, length), target.AsSpan(offset, length), SvnDiffVersion.One));
        }
        delta.Position = 0;
        await using var sourceStream = new MemoryStream(source, writable: false);
        await using var result = new MemoryStream();

        await SvnDiffDecoder.ApplyAsync(sourceStream, delta, result);

        Assert.Equal(target, result.ToArray());
    }

    [Fact]
    public void DecoderRejectsTruncatedWindow()
    {
        var encoded = SvnDiffEncoder.Encode("source"u8, "target"u8, SvnDiffVersion.One);

        Assert.Throws<InvalidDataException>(() => SvnDiffDecoder.Apply("source"u8, encoded[..^1]));
    }

    [Theory]
    [InlineData(SvnDiffVersion.Zero)]
    [InlineData(SvnDiffVersion.One)]
    public void InspectorDecodesWindowsAndInstructions(SvnDiffVersion version)
    {
        var source = Encoding.UTF8.GetBytes("prefix: reusable text");
        var target = Encoding.UTF8.GetBytes("prefix: reusable text plus plus");

        var inspection = SvnDiffInspector.Inspect(SvnDiffEncoder.Encode(source, target, version));

        Assert.Equal(version, inspection.Version);
        var window = Assert.Single(inspection.Windows);
        Assert.Equal(target.Length, window.TargetLength);
        Assert.NotEmpty(window.Instructions);
        Assert.Equal(target.Length, window.Instructions.Sum(instruction => instruction.Length));
    }
}

using System.Text;
using SvnFlux.RaSvn.Wire;

namespace SvnFlux.RaSvn.Tests;

public sealed class SvnWireReaderTests
{
    [Fact]
    public async Task ReadsExternallySpecifiedMixedItemExample()
    {
        await using var stream = new MemoryStream("( word 22 6:string ( sublist ) ) "u8.ToArray());
        var reader = new SvnWireReader(stream);

        var result = Assert.IsType<SvnWireList>(await reader.ReadItemAsync());

        Assert.Equal(4, result.Items.Count);
        Assert.Equal("word", Assert.IsType<SvnWireWord>(result.Items[0]).Value);
        Assert.Equal(22, Assert.IsType<SvnWireNumber>(result.Items[1]).Value);
        Assert.Equal("string", Encoding.ASCII.GetString(Assert.IsType<SvnWireString>(result.Items[2]).Value.Span));
    }

    [Fact]
    public async Task PreservesBinaryStrings()
    {
        await using var stream = new MemoryStream([(byte)'3', (byte)':', 0, 0xff, 0x80, (byte)' ']);
        var reader = new SvnWireReader(stream);

        var result = Assert.IsType<SvnWireString>(await reader.ReadItemAsync());

        Assert.Equal(new byte[] { 0, 0xff, 0x80 }, result.Value.ToArray());
    }

    [Fact]
    public async Task RejectsTruncatedString()
    {
        await using var stream = new MemoryStream("5:abc"u8.ToArray());
        var reader = new SvnWireReader(stream);

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReadItemAsync());
    }

    [Fact]
    public async Task EnforcesNestingLimit()
    {
        await using var stream = new MemoryStream("( ( ( ) ) ) "u8.ToArray());
        var reader = new SvnWireReader(stream, new SvnWireLimits { MaximumListDepth = 2 });

        await Assert.ThrowsAsync<SvnWireProtocolException>(async () => await reader.ReadItemAsync());
    }
}

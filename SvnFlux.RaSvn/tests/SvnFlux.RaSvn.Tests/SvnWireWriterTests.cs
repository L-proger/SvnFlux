using System.Text;
using SvnFlux.RaSvn.Wire;

namespace SvnFlux.RaSvn.Tests;

public sealed class SvnWireWriterTests
{
    [Fact]
    public async Task WritesCanonicalMixedList()
    {
        await using var stream = new MemoryStream();
        var writer = new SvnWireWriter(stream);
        var item = new SvnWireList([
            new SvnWireWord("word"),
            new SvnWireNumber(22),
            new SvnWireString("string"u8.ToArray()),
            new SvnWireList([new SvnWireWord("sublist")])
        ]);

        await writer.WriteItemAsync(item);

        Assert.Equal("( word 22 6:string ( sublist ) ) ", Encoding.ASCII.GetString(stream.ToArray()));
    }
}

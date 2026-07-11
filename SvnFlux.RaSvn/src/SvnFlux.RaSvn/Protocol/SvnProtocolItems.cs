using System.Text;
using SvnFlux.RaSvn.Wire;

namespace SvnFlux.RaSvn.Protocol;

internal static class SvnProtocolItems
{
    public static SvnWireNumber Number(long value) => new(value);
    public static SvnWireWord Word(string value) => new(value);
    public static SvnWireString Text(string value) => new(Encoding.UTF8.GetBytes(value));
    public static SvnWireList List(params SvnWireItem[] items) => new(items);
    public static SvnWireList EmptyList() => new([]);
    public static SvnWireList Success(params SvnWireItem[] parameters) => List(Word("success"), List(parameters));

    public static string GetText(SvnWireItem item, string fieldName)
    {
        if (item is not SvnWireString value)
        {
            throw new SvnWireProtocolException($"Expected string field '{fieldName}'.");
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(value.Value.Span);
        }
        catch (DecoderFallbackException)
        {
            throw new SvnWireProtocolException($"Field '{fieldName}' is not valid UTF-8.");
        }
    }
}

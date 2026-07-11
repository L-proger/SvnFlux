namespace SvnFlux.Svndiff;

public static class SvnDiffLiteralEncoder
{
    public static byte[] EncodeHeader() => SvnDiffEncoder.EncodeHeader(SvnDiffVersion.Zero);

    public static byte[] EncodeWindow(ReadOnlySpan<byte> targetData)
    {
        if (targetData.IsEmpty)
        {
            throw new ArgumentException("A literal svndiff window cannot be empty.", nameof(targetData));
        }

        var window = new SvnDiffWindow(
            0,
            targetData.Length,
            [new SvnDiffInstruction(SvnDiffInstructionKind.NewData, 0, targetData.Length)],
            targetData.ToArray());
        return SvnDiffEncoder.EncodeWindow(0, window, SvnDiffVersion.Zero);
    }

    public static byte[] Encode(ReadOnlySpan<byte> targetData)
    {
        var header = EncodeHeader();
        if (targetData.IsEmpty)
        {
            return header;
        }

        var window = EncodeWindow(targetData);
        var result = new byte[header.Length + window.Length];
        header.CopyTo(result, 0);
        window.CopyTo(result, header.Length);
        return result;
    }
}

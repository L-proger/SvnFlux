using System.Globalization;
using System.Text;

namespace SvnFlux.RaSvn.Wire;

public sealed class SvnWireWriter
{
    private static readonly byte[] Space = " "u8.ToArray();
    private readonly Stream _stream;

    public SvnWireWriter(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _stream = stream;
    }

    public async ValueTask WriteItemAsync(SvnWireItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await WriteItemCoreAsync(item, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteItemCoreAsync(SvnWireItem item, CancellationToken cancellationToken)
    {
        switch (item)
        {
            case SvnWireNumber number when number.Value >= 0:
                await WriteAsciiAsync(number.Value.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
                await _stream.WriteAsync(Space, cancellationToken).ConfigureAwait(false);
                break;
            case SvnWireWord word:
                ValidateWord(word.Value);
                await WriteAsciiAsync(word.Value, cancellationToken).ConfigureAwait(false);
                await _stream.WriteAsync(Space, cancellationToken).ConfigureAwait(false);
                break;
            case SvnWireString value:
                await WriteAsciiAsync(value.Value.Length.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
                await _stream.WriteAsync(":"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                await _stream.WriteAsync(value.Value, cancellationToken).ConfigureAwait(false);
                await _stream.WriteAsync(Space, cancellationToken).ConfigureAwait(false);
                break;
            case SvnWireList list:
                await _stream.WriteAsync("( "u8.ToArray(), cancellationToken).ConfigureAwait(false);
                foreach (var child in list.Items)
                {
                    await WriteItemCoreAsync(child, cancellationToken).ConfigureAwait(false);
                }

                await _stream.WriteAsync(") "u8.ToArray(), cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(item), "Unsupported or invalid ra_svn item.");
        }
    }

    private async ValueTask WriteAsciiAsync(string value, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(value), cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateWord(string value)
    {
        if (value.Length is 0 or > 31 || !value.All(character =>
                character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
        {
            throw new ArgumentException("An ra_svn word may contain only ASCII letters, digits and hyphens and is limited to 31 characters.", nameof(value));
        }
    }
}

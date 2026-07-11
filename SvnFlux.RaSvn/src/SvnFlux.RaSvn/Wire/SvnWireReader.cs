using System.Buffers;
using System.Globalization;
using System.Text;
using System.Net.Sockets;

namespace SvnFlux.RaSvn.Wire;

public sealed class SvnWireReader
{
    private readonly Stream _stream;
    private readonly SvnWireLimits _limits;
    private int _lookahead = -1;

    public SvnWireReader(Stream stream, SvnWireLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        _stream = stream;
        _limits = limits ?? new SvnWireLimits();
    }

    internal bool HasDataAvailable => _lookahead >= 0 || _stream is NetworkStream { DataAvailable: true };

    public async ValueTask<SvnWireItem> ReadItemAsync(CancellationToken cancellationToken = default)
    {
        await SkipWhitespaceAsync(cancellationToken).ConfigureAwait(false);
        return await ReadItemCoreAsync(0, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SvnWireItem> ReadItemCoreAsync(int depth, CancellationToken cancellationToken)
    {
        var first = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        if (first < 0)
        {
            throw new EndOfStreamException("The ra_svn item was truncated.");
        }

        if (first == '(')
        {
            return await ReadListAsync(depth, cancellationToken).ConfigureAwait(false);
        }

        if (IsDigit(first))
        {
            return await ReadNumberOrStringAsync(first, cancellationToken).ConfigureAwait(false);
        }

        if (IsAlpha(first))
        {
            return await ReadWordAsync(first, cancellationToken).ConfigureAwait(false);
        }

        throw new SvnWireProtocolException($"Unexpected byte 0x{first:X2} at the start of an item.");
    }

    private async ValueTask<SvnWireList> ReadListAsync(int depth, CancellationToken cancellationToken)
    {
        if (depth >= _limits.MaximumListDepth)
        {
            throw new SvnWireProtocolException("Maximum ra_svn list nesting depth exceeded.");
        }

        await RequireWhitespaceAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<SvnWireItem>();
        while (true)
        {
            await SkipWhitespaceAsync(cancellationToken).ConfigureAwait(false);
            var next = await PeekByteAsync(cancellationToken).ConfigureAwait(false);
            if (next < 0)
            {
                throw new EndOfStreamException("The ra_svn list was truncated.");
            }

            if (next == ')')
            {
                await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                await RequireWhitespaceAsync(cancellationToken).ConfigureAwait(false);
                return new SvnWireList(items);
            }

            if (items.Count >= _limits.MaximumItemsPerList)
            {
                throw new SvnWireProtocolException("Maximum number of ra_svn list items exceeded.");
            }

            items.Add(await ReadItemCoreAsync(depth + 1, cancellationToken).ConfigureAwait(false));
        }
    }

    private async ValueTask<SvnWireItem> ReadNumberOrStringAsync(int first, CancellationToken cancellationToken)
    {
        var digits = new StringBuilder().Append((char)first);
        while (true)
        {
            var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (next == ':')
            {
                if (!int.TryParse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                    length > _limits.MaximumStringLength)
                {
                    throw new SvnWireProtocolException("Invalid or oversized ra_svn string length.");
                }

                var bytes = new byte[length];
                await _stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                await RequireWhitespaceAsync(cancellationToken).ConfigureAwait(false);
                return new SvnWireString(bytes);
            }

            if (IsWhitespace(next))
            {
                if (!long.TryParse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    throw new SvnWireProtocolException("Invalid ra_svn number.");
                }

                return new SvnWireNumber(number);
            }

            if (!IsDigit(next) || digits.Length >= 19)
            {
                throw new SvnWireProtocolException("Invalid or overflowing ra_svn number.");
            }

            digits.Append((char)next);
        }
    }

    private async ValueTask<SvnWireWord> ReadWordAsync(int first, CancellationToken cancellationToken)
    {
        var bytes = new ArrayBufferWriter<byte>();
        bytes.GetSpan(1)[0] = (byte)first;
        bytes.Advance(1);

        while (true)
        {
            var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (IsWhitespace(next))
            {
                return new SvnWireWord(Encoding.ASCII.GetString(bytes.WrittenSpan));
            }

            if (!(IsAlpha(next) || IsDigit(next) || next == '-') || bytes.WrittenCount >= _limits.MaximumWordLength)
            {
                throw new SvnWireProtocolException("Invalid or oversized ra_svn word.");
            }

            bytes.GetSpan(1)[0] = (byte)next;
            bytes.Advance(1);
        }
    }

    private async ValueTask RequireWhitespaceAsync(CancellationToken cancellationToken)
    {
        var value = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        if (!IsWhitespace(value))
        {
            throw new SvnWireProtocolException("Mandatory whitespace is missing.");
        }
    }

    private async ValueTask SkipWhitespaceAsync(CancellationToken cancellationToken)
    {
        while (IsWhitespace(await PeekByteAsync(cancellationToken).ConfigureAwait(false)))
        {
            await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<int> PeekByteAsync(CancellationToken cancellationToken)
    {
        if (_lookahead < 0)
        {
            _lookahead = await ReadStreamByteAsync(cancellationToken).ConfigureAwait(false);
        }

        return _lookahead;
    }

    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_lookahead >= 0)
        {
            var value = _lookahead;
            _lookahead = -1;
            return value;
        }

        return await ReadStreamByteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<int> ReadStreamByteAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        return await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) == 0 ? -1 : buffer[0];
    }

    private static bool IsWhitespace(int value) => value is ' ' or '\n';
    private static bool IsDigit(int value) => value is >= '0' and <= '9';
    private static bool IsAlpha(int value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}

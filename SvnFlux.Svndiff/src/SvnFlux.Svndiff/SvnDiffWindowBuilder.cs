using System.Buffers;
using System.Buffers.Binary;

namespace SvnFlux.Svndiff;

public static class SvnDiffWindowBuilder
{
    private const int IndexKeyLength = sizeof(uint);
    private const int MinimumCopyLength = 8;
    private const int MaximumSourceCandidates = 32;

    public static SvnDiffWindow Build(ReadOnlySpan<byte> source, ReadOnlySpan<byte> target)
    {
        if (target.IsEmpty)
        {
            throw new ArgumentException("A svndiff window target view cannot be empty.", nameof(target));
        }

        var sourceIndex = BuildSourceIndex(source);
        var targetIndex = new Dictionary<uint, int>();
        var instructions = new List<SvnDiffInstruction>();
        var newData = new ArrayBufferWriter<byte>();
        var position = 0;
        var literalStart = 0;

        while (position < target.Length)
        {
            var sourceMatch = FindSourceMatch(source, target, position, sourceIndex);
            var targetMatch = FindTargetMatch(target, position, targetIndex);
            var best = sourceMatch.Length >= targetMatch.Length ? sourceMatch : targetMatch;
            if (best.Length < MinimumCopyLength)
            {
                AddTargetIndex(target, position, targetIndex);
                position++;
                continue;
            }

            FlushLiteral(target, literalStart, position, instructions, newData);
            instructions.Add(new SvnDiffInstruction(best.Kind, best.Offset, best.Length));
            var end = position + best.Length;
            while (position < end)
            {
                AddTargetIndex(target, position, targetIndex);
                position++;
            }

            literalStart = position;
        }

        FlushLiteral(target, literalStart, target.Length, instructions, newData);
        return new SvnDiffWindow(source.Length, target.Length, instructions, newData.WrittenMemory.ToArray());
    }

    private static Dictionary<uint, List<int>> BuildSourceIndex(ReadOnlySpan<byte> source)
    {
        var result = new Dictionary<uint, List<int>>();
        for (var offset = 0; offset <= source.Length - IndexKeyLength; offset++)
        {
            var key = ReadKey(source, offset);
            if (!result.TryGetValue(key, out var offsets))
            {
                offsets = [];
                result.Add(key, offsets);
            }

            if (offsets.Count < MaximumSourceCandidates)
            {
                offsets.Add(offset);
            }
        }

        return result;
    }

    private static Match FindSourceMatch(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> target,
        int targetOffset,
        IReadOnlyDictionary<uint, List<int>> sourceIndex)
    {
        if (targetOffset > target.Length - IndexKeyLength ||
            !sourceIndex.TryGetValue(ReadKey(target, targetOffset), out var candidates))
        {
            return default;
        }

        var bestOffset = 0;
        var bestLength = 0;
        foreach (var sourceOffset in candidates)
        {
            var length = CommonPrefixLength(source[sourceOffset..], target[targetOffset..]);
            if (length > bestLength)
            {
                bestOffset = sourceOffset;
                bestLength = length;
            }
        }

        return new Match(SvnDiffInstructionKind.Source, bestOffset, bestLength);
    }

    private static Match FindTargetMatch(
        ReadOnlySpan<byte> target,
        int targetOffset,
        IReadOnlyDictionary<uint, int> targetIndex)
    {
        if (targetOffset > target.Length - IndexKeyLength ||
            !targetIndex.TryGetValue(ReadKey(target, targetOffset), out var previousOffset))
        {
            return default;
        }

        var maximumLength = Math.Min(targetOffset - previousOffset, target.Length - targetOffset);
        var length = CommonPrefixLength(
            target.Slice(previousOffset, maximumLength),
            target.Slice(targetOffset, maximumLength));
        return new Match(SvnDiffInstructionKind.Target, previousOffset, length);
    }

    private static int CommonPrefixLength(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var length = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < length && left[index] == right[index])
        {
            index++;
        }

        return index;
    }

    private static void FlushLiteral(
        ReadOnlySpan<byte> target,
        int start,
        int end,
        List<SvnDiffInstruction> instructions,
        IBufferWriter<byte> newData)
    {
        var length = end - start;
        if (length == 0)
        {
            return;
        }

        instructions.Add(new SvnDiffInstruction(SvnDiffInstructionKind.NewData, 0, length));
        newData.Write(target[start..end]);
    }

    private static void AddTargetIndex(ReadOnlySpan<byte> target, int offset, IDictionary<uint, int> targetIndex)
    {
        if (offset <= target.Length - IndexKeyLength)
        {
            targetIndex[ReadKey(target, offset)] = offset;
        }
    }

    private static uint ReadKey(ReadOnlySpan<byte> value, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(value[offset..]);

    private readonly record struct Match(SvnDiffInstructionKind Kind, int Offset, int Length);
}

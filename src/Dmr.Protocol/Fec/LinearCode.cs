using System.Numerics;

namespace Dmr.Fec;

public readonly record struct CodeResult(uint Data, uint Word, int Corrections, bool Valid);

/// <summary>Systematic binary linear code with a bounded-distance syndrome decoder.</summary>
public sealed class LinearCode
{
    private readonly uint[] words;
    private readonly Dictionary<uint, uint> errors = new();
    public int Length { get; }
    public int DataBits { get; }
    public int Radius { get; }
    public LinearCode(int length, int dataBits, int radius, uint[] parityRows)
    {
        Length = length; DataBits = dataBits; Radius = radius;
        if (parityRows.Length != dataBits) throw new ArgumentException("Invalid generator matrix.");
        words = new uint[1 << dataBits];
        for (uint d = 0; d < words.Length; d++)
        {
            uint parity = 0;
            for (int i = 0; i < dataBits; i++) if ((d & (1u << (dataBits - 1 - i))) != 0) parity ^= parityRows[i];
            words[d] = (d << (length - dataBits)) | parity;
        }
        AddErrors(0, 0, radius);
    }
    private uint Syndrome(uint word) => word ^ words[word >> (Length - DataBits)];
    private void AddErrors(uint word, int start, int remaining)
    {
        uint syndrome = Syndrome(word);
        if (!errors.TryAdd(syndrome, word) && errors[syndrome] != word)
            throw new ArgumentException("Correction radius exceeds unique decoding distance.");
        if (remaining == 0) return;
        for (int i = start; i < Length; i++) AddErrors(word | (1u << i), i + 1, remaining - 1);
    }
    public uint Encode(uint data) => words[data];
    public byte[] EncodeBits(uint data) => Bits.FromUInt(Encode(data), Length);
    public CodeResult Decode(uint word)
    {
        if (word >= (1u << Length)) throw new ArgumentOutOfRangeException(nameof(word));
        if (!errors.TryGetValue(Syndrome(word), out uint error)) return new(word >> (Length - DataBits), word, 0, false);
        uint corrected = word ^ error;
        return new(corrected >> (Length - DataBits), corrected, BitOperations.PopCount(error), true);
    }
    public CodeResult Decode(ReadOnlySpan<byte> bits)
    {
        if (bits.Length != Length) throw new ArgumentException("Incorrect codeword length.");
        return Decode(Bits.UInt(bits));
    }
}

namespace Dmr.Fec;

public readonly record struct TrellisResult(byte[] Data, int Corrections);

/// <summary>Rate 3/4 trellis code of TS 102 361-1 Annex B.2.4 (tables B.7 to B.10) and the rate 1 bit layout of B.2.5.
/// All arrays are unpacked bits, first transmitted bit first; the 196 info bits are the burst without SYNC and SLOT.</summary>
public static class Trellis
{
    // Table B.7: constellation point for [state, input tribit]. The next state is the input tribit.
    private static readonly byte[] Transition =
    [
        0, 8, 4, 12, 2, 10, 6, 14,
        4, 12, 2, 10, 6, 14, 0, 8,
        1, 9, 5, 13, 3, 11, 7, 15,
        5, 13, 3, 11, 7, 15, 1, 9,
        3, 11, 7, 15, 1, 9, 5, 13,
        7, 15, 1, 9, 5, 13, 3, 11,
        2, 10, 6, 14, 0, 8, 4, 12,
        6, 14, 0, 8, 4, 12, 2, 10,
    ];
    // Table B.8: constellation point to (dibit 0, dibit 1) symbol amplitudes.
    private static readonly sbyte[] PointSymbols =
    [
        +1, -1,  -1, -1,  +3, -3,  -3, -3,  -3, -1,  +3, -1,  -1, -3,  +1, -3,
        -3, +3,  +3, +3,  -1, +1,  +1, +1,  +1, +3,  -1, +3,  +3, +1,  -3, +1,
    ];
    /// <summary>Each point as four air bits (dibit 0 then dibit 1), with the DMR symbol mapping +1=00, +3=01, -1=10, -3=11.</summary>
    private static readonly byte[] PointBits = BuildPointBits();
    /// <summary>Symbol level of an air dibit (00 = +1, 01 = +3, 10 = -1, 11 = -3).</summary>
    private static readonly sbyte[] DibitLevel = [1, 3, -1, -3];
    /// <summary>Table B.9/B.10: transmitted dibit i carries encoder dibit Interleave[i].</summary>
    private static readonly byte[] Interleave = BuildInterleave();
    private static byte[] BuildPointBits()
    {
        static int Dibit(int symbol) => symbol switch { 1 => 0, 3 => 1, -1 => 2, -3 => 3, _ => throw new InvalidOperationException() };
        var bits = new byte[16];
        for (int p = 0; p < 16; p++) bits[p] = (byte)((Dibit(PointSymbols[2 * p]) << 2) | Dibit(PointSymbols[2 * p + 1]));
        return bits;
    }
    private static byte[] BuildInterleave()
    {
        var table = new byte[98]; int i = 0;
        for (int offset = 0; offset < 8; offset += 2)
            for (int e = offset; e + 1 < 98; e += 8) { table[i++] = (byte)e; table[i++] = (byte)(e + 1); }
        if (i != 98) throw new InvalidOperationException("Interleave table size.");
        return table;
    }
    /// <summary>Encodes 144 data bits (18 octets) to the 196 transmitted info bits.</summary>
    public static byte[] Encode34(ReadOnlySpan<byte> data)
    {
        if (data.Length != 144) throw new ArgumentException("Rate 3/4 trellis requires 144 bits.");
        Span<byte> enc = stackalloc byte[98];
        int state = 0;
        for (int t = 0; t < 49; t++)
        {
            int tribit = t < 48 ? (data[3 * t] << 2) | (data[3 * t + 1] << 1) | data[3 * t + 2] : 0;
            int bits = PointBits[Transition[state * 8 + tribit]];
            enc[2 * t] = (byte)(bits >> 2); enc[2 * t + 1] = (byte)(bits & 3);
            state = tribit;
        }
        var air = new byte[196];
        for (int i = 0; i < 98; i++) { int d = enc[Interleave[i]]; air[2 * i] = (byte)(d >> 1); air[2 * i + 1] = (byte)(d & 1); }
        return air;
    }
    /// <summary>Viterbi decoding of hard-decision symbols; the path starts and, after the flushing tribit, ends in state 0. The branch
    /// metric is the squared distance between received and expected levels (in units of one level step), the hard-decision form of
    /// the Euclidean metric this trellis-coded modulation is designed for: errors to an adjacent level are cheap, larger jumps are
    /// treated as the improbable events they are. <see cref="TrellisResult.Corrections"/> is the bit distance between the input
    /// and the re-encoded decision.
    /// The code has no integrity check of its own: callers rely on the CRC-9 or message CRC.</summary>
    public static TrellisResult Decode34(ReadOnlySpan<byte> air)
    {
        if (air.Length != 196) throw new ArgumentException("Rate 3/4 trellis requires 196 bits.");
        Span<byte> enc = stackalloc byte[98];
        for (int i = 0; i < 98; i++) enc[Interleave[i]] = (byte)((air[2 * i] << 1) | air[2 * i + 1]);
        Span<int> metric = stackalloc int[8], next = stackalloc int[8];
        Span<byte> back = stackalloc byte[49 * 8];
        const int Unreachable = 1 << 20;
        metric.Fill(Unreachable); metric[0] = 0;
        for (int t = 0; t < 49; t++)
        {
            int inputs = t < 48 ? 8 : 1;
            next.Fill(Unreachable);
            for (int input = 0; input < inputs; input++)
            {
                int best = Unreachable, from = 0;
                for (int s = 0; s < 8; s++)
                {
                    if (metric[s] >= Unreachable) continue;
                    int point = Transition[s * 8 + input];
                    int d0 = (PointSymbols[2 * point] - DibitLevel[enc[2 * t]]) / 2, d1 = (PointSymbols[2 * point + 1] - DibitLevel[enc[2 * t + 1]]) / 2;
                    int cost = metric[s] + d0 * d0 + d1 * d1;
                    if (cost < best) { best = cost; from = s; }
                }
                next[input] = best; back[t * 8 + input] = (byte)from;
            }
            next.CopyTo(metric);
        }
        var data = new byte[144];
        int state = 0;
        for (int t = 48; t >= 1; t--)
        {
            int previous = back[t * 8 + state];
            // The state after step t-1 is the tribit input at step t-1.
            int tribit = previous; int k = 3 * (t - 1);
            data[k] = (byte)((tribit >> 2) & 1); data[k + 1] = (byte)((tribit >> 1) & 1); data[k + 2] = (byte)(tribit & 1);
            state = previous;
        }
        return new(data, Bits.Distance(Encode34(data), air));
    }
    /// <summary>Rate 1 (B.2.5, table B.10B): 192 data bits around four zero pad bits at info positions 96 to 99.</summary>
    public static byte[] EncodeRate1(ReadOnlySpan<byte> data)
    {
        if (data.Length != 192) throw new ArgumentException("Rate 1 requires 192 bits.");
        var air = new byte[196];
        data[..96].CopyTo(air); data[96..].CopyTo(air.AsSpan(100));
        return air;
    }
    public static byte[] DecodeRate1(ReadOnlySpan<byte> air)
    {
        if (air.Length != 196) throw new ArgumentException("Rate 1 requires 196 bits.");
        var data = new byte[192];
        air[..96].CopyTo(data); air[100..].CopyTo(data.AsSpan(96));
        return data;
    }
}

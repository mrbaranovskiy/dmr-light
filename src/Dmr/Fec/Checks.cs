namespace Dmr.Fec;

public static class Checks
{
    public static uint Crc(ReadOnlySpan<byte> bits, int width, uint polynomial, uint initial = 0, uint finalXor = 0)
    {
        uint value = initial; uint mask = width == 32 ? uint.MaxValue : (1u << width) - 1;
        foreach (byte bit in bits)
        {
            uint feedback = (value >> (width - 1)) ^ bit;
            value = (value << 1) & mask;
            if ((feedback & 1) != 0) value ^= polynomial;
        }
        return (value ^ finalXor) & mask;
    }
    private static readonly byte[][] RsRows =
    [
        [0x1c,0xbc,0xfd], [0x89,0x31,0x08], [0xad,0x41,0x36],
        [0x7d,0x71,0x16], [0xf3,0xa6,0x3a], [0x08,0x83,0x7b],
        [0x3f,0x6f,0x02], [0x6c,0x0d,0xa7], [0x0e,0x38,0x40]
    ];
    public static byte GfMultiply(int a, int b)
    {
        int value = 0;
        for (; b != 0; b >>= 1)
        {
            if ((b & 1) != 0) value ^= a;
            a <<= 1; if ((a & 256) != 0) a ^= 0x11d;
        }
        return (byte)value;
    }
    public static byte[] RsParity(ReadOnlySpan<byte> data)
    {
        if (data.Length != 9) throw new ArgumentException("RS (12,9) requires nine bytes.");
        var p = new byte[3];
        for (int i = 0; i < 9; i++) for (int j = 0; j < 3; j++) p[j] ^= GfMultiply(data[i], RsRows[i][j]);
        return p;
    }
    public static bool CorrectRs(Span<byte> word, out int correctedBytes)
    {
        if (word.Length != 12) throw new ArgumentException("RS word requires 12 bytes.");
        var p = RsParity(word[..9]);
        byte[] s = [(byte)(p[0] ^ word[9]), (byte)(p[1] ^ word[10]), (byte)(p[2] ^ word[11])];
        correctedBytes = 0;
        if (s.All(x => x == 0)) return true;
        // Small shortened code: exhaustive one-symbol syndrome solution is bounded and unambiguous.
        for (int position = 0; position < 12; position++)
            for (int error = 1; error < 256; error++)
            {
                bool match = true;
                for (int j = 0; j < 3; j++)
                    match &= s[j] == (position < 9 ? GfMultiply(error, RsRows[position][j]) : position - 9 == j ? error : 0);
                if (!match) continue;
                word[position] ^= (byte)error; correctedBytes = 1; return true;
            }
        return false;
    }
}

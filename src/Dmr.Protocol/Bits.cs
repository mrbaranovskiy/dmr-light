namespace Dmr;

/// <summary>All arrays are in transmission order, most significant bit first.</summary>
public static class Bits
{
    public static byte[] FromUInt(uint value, int count)
    {
        var bits = new byte[count];
        for (int i = 0; i < count; i++) bits[i] = (byte)((value >> (count - 1 - i)) & 1);
        return bits;
    }
    public static uint UInt(ReadOnlySpan<byte> bits)
    {
        uint value = 0;
        foreach (byte bit in bits) value = (value << 1) | bit;
        return value;
    }
    public static byte[] Unpack(ReadOnlySpan<byte> data)
    {
        var result = new byte[data.Length * 8];
        for (int i = 0; i < result.Length; i++) result[i] = (byte)((data[i / 8] >> (7 - i % 8)) & 1);
        return result;
    }
    public static byte[] Pack(ReadOnlySpan<byte> bits)
    {
        var result = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++) result[i / 8] |= (byte)(bits[i] << (7 - i % 8));
        return result;
    }
    public static string Hex(ReadOnlySpan<byte> bits) => Convert.ToHexString(Pack(bits));
    public static int Distance(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Bit lengths differ.");
        int n = 0;
        for (int i = 0; i < a.Length; i++) n += a[i] == b[i] ? 0 : 1;
        return n;
    }
}

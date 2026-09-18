namespace Dmr.Fec;

public sealed record BlockResult(byte[] Data, bool Valid, int Corrections);

public static class ProductCodes
{
    public static byte[] Encode196(ReadOnlySpan<byte> data)
    {
        if (data.Length != 96) throw new ArgumentException("Expected 96 bits.");
        var m = new byte[196]; int k = 0;
        for (int r = 0; r < 9; r++)
        {
            for (int c = r == 0 ? 3 : 0; c < 11; c++) m[1 + r * 15 + c] = data[k++];
            Codes.Hamming15.EncodeBits(Bits.UInt(m.AsSpan(1 + r * 15, 11))).CopyTo(m, 1 + r * 15);
        }
        for (int c = 0; c < 15; c++)
        {
            uint d = 0; for (int r = 0; r < 9; r++) d = (d << 1) | m[1 + r * 15 + c];
            var col = Codes.Hamming13.EncodeBits(d);
            for (int r = 0; r < 13; r++) m[1 + r * 15 + c] = col[r];
        }
        var air = new byte[196];
        for (int i = 0; i < 196; i++) air[i * 181 % 196] = m[i];
        return air;
    }
    public static BlockResult Decode196(ReadOnlySpan<byte> air)
    {
        if (air.Length != 196) throw new ArgumentException("Expected 196 bits.");
        var m = new byte[196];
        for (int i = 0; i < 196; i++) m[i] = air[i * 181 % 196];
        var original = (byte[])m.Clone();
        for (int pass = 0; pass < 5; pass++)
        {
            bool changed = false;
            for (int c = 0; c < 15; c++) changed |= Correct(m, 1 + c, 15, Codes.Hamming13);
            for (int r = 0; r < 9; r++) changed |= Correct(m, 1 + r * 15, 1, Codes.Hamming15);
            if (!changed) break;
        }
        bool valid = true;
        for (int c = 0; c < 15; c++) valid &= Clean(m, 1 + c, 15, Codes.Hamming13);
        for (int r = 0; r < 9; r++) valid &= Clean(m, 1 + r * 15, 1, Codes.Hamming15);
        // R(3) is unprotected. The three systematic reserved bits are protected.
        valid &= m[1] == 0 && m[2] == 0 && m[3] == 0;
        var data = new byte[96]; int k = 0;
        for (int r = 0; r < 9; r++)
            for (int c = r == 0 ? 3 : 0; c < 11; c++) data[k++] = m[1 + r * 15 + c];
        return new(data, valid, Bits.Distance(original, m));
    }
    private static bool Correct(byte[] matrix, int start, int stride, LinearCode code)
    {
        uint w = 0;
        for (int i = 0; i < code.Length; i++) w = (w << 1) | matrix[start + i * stride];
        var decoded = code.Decode(w);
        if (!decoded.Valid || decoded.Corrections == 0) return false;
        for (int i = 0; i < code.Length; i++) matrix[start + i * stride] = (byte)((decoded.Word >> (code.Length - 1 - i)) & 1);
        return true;
    }
    private static bool Clean(byte[] matrix, int start, int stride, LinearCode code)
    {
        uint w = 0;
        for (int i = 0; i < code.Length; i++) w = (w << 1) | matrix[start + i * stride];
        var d = code.Decode(w);
        return d.Valid && d.Corrections == 0;
    }
    private static byte[] Transpose(byte[] matrix, int rows, int columns)
    {
        var air = new byte[matrix.Length];
        for (int r = 0; r < rows; r++) for (int c = 0; c < columns; c++) air[c * rows + r] = matrix[r * columns + c];
        return air;
    }
    private static byte[] EncodeVariable(ReadOnlySpan<byte> data, int rows, LinearCode code)
    {
        var m = new byte[rows * code.Length];
        for (int r = 0; r < rows - 1; r++) code.EncodeBits(Bits.UInt(data.Slice(r * code.DataBits, code.DataBits))).CopyTo(m, r * code.Length);
        for (int c = 0; c < code.Length; c++) for (int r = 0; r < rows - 1; r++) m[(rows - 1) * code.Length + c] ^= m[r * code.Length + c];
        return Transpose(m, rows, code.Length);
    }
    private static BlockResult DecodeVariable(ReadOnlySpan<byte> air, int rows, LinearCode code)
    {
        if (air.Length != rows * code.Length) throw new ArgumentException("Wrong variable BPTC length.");
        var m = new byte[air.Length];
        for (int r = 0; r < rows; r++) for (int c = 0; c < code.Length; c++) m[r * code.Length + c] = air[c * rows + r];
        var original = (byte[])m.Clone();
        for (int r = 0; r < rows - 1; r++) Correct(m, r * code.Length, 1, code);
        bool valid = true;
        for (int r = 0; r < rows - 1; r++) valid &= Clean(m, r * code.Length, 1, code);
        for (int c = 0; c < code.Length; c++)
        {
            byte parity = 0; for (int r = 0; r < rows; r++) parity ^= m[r * code.Length + c];
            valid &= parity == 0;
        }
        var data = new byte[(rows - 1) * code.DataBits];
        for (int r = 0; r < rows - 1; r++) Array.Copy(m, r * code.Length, data, r * code.DataBits, code.DataBits);
        return new(data, valid, Bits.Distance(m, original));
    }
    public static byte[] EncodeEmbedded(ReadOnlySpan<byte> lc)
    {
        if (lc.Length != 72) throw new ArgumentException("Expected 72 LC bits.");
        var data = new byte[77]; int k = 0;
        uint sum = (uint)(Bits.Pack(lc).Sum(x => x) % 31);
        for (int r = 0; r < 7; r++)
        {
            int len = r < 2 ? 11 : 10;
            lc.Slice(k, len).CopyTo(data.AsSpan(r * 11)); k += len;
            if (r >= 2) data[r * 11 + 10] = (byte)((sum >> (6 - r)) & 1);
        }
        return EncodeVariable(data, 8, Codes.Hamming16);
    }
    public static BlockResult DecodeEmbedded(ReadOnlySpan<byte> air)
    {
        var decoded = DecodeVariable(air, 8, Codes.Hamming16);
        var lc = new byte[72]; int k = 0; uint check = 0;
        for (int r = 0; r < 7; r++)
        {
            int len = r < 2 ? 11 : 10;
            decoded.Data.AsSpan(r * 11, len).CopyTo(lc.AsSpan(k)); k += len;
            if (r >= 2) check = (check << 1) | decoded.Data[r * 11 + 10];
        }
        return new(lc, decoded.Valid && Bits.Pack(lc).Sum(x => x) % 31 == check, decoded.Corrections);
    }
    public static byte[] EncodeShortLc(ReadOnlySpan<byte> lc)
    {
        if (lc.Length != 28) throw new ArgumentException("Expected 28 Short LC bits.");
        var data = new byte[36]; lc.CopyTo(data);
        Bits.FromUInt(Checks.Crc(lc, 8, 0x07), 8).CopyTo(data, 28);
        return EncodeVariable(data, 4, Codes.Hamming17);
    }
    public static BlockResult DecodeShortLc(ReadOnlySpan<byte> air)
    {
        var d = DecodeVariable(air, 4, Codes.Hamming17);
        return new(d.Data[..28], d.Valid && Checks.Crc(d.Data.AsSpan(0, 28), 8, 7) == Bits.UInt(d.Data.AsSpan(28)), d.Corrections);
    }
}

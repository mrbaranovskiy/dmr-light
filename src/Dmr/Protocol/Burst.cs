using Dmr.Fec;

namespace Dmr.Protocol;

public static class Burst
{
    public static readonly IReadOnlyDictionary<string, string> SyncHex = new Dictionary<string,string>
    {
        ["bs_voice"]="755FD7DF75F7", ["bs_data"]="DFF57D75DF5D",
        ["ms_voice"]="7F7D5DD57DFD", ["ms_data"]="D5D7F77FD757",
        ["direct1_voice"]="5D577F7757FF", ["direct1_data"]="F7FDD5DDFD55",
        ["direct2_voice"]="7DFFD5F55D5F", ["direct2_data"]="D7557F5FF7F5",
        ["ms_rc"]="77D55F7DFD77"
    };
    public static readonly string[] DataTypes = ["pi_header","voice_lc_header","terminator_lc","csbk","mbc_header","mbc_continuation","data_header","rate_half","rate_three_quarters","idle","rate_one","reserved11","reserved12","reserved13","reserved14","reserved15"];
    public static readonly int[] TactPositions = [0,4,8,12,14,18,22];
    public static byte[] Sync(string kind) => Bits.Unpack(Convert.FromHexString(SyncHex[kind]));
    public static byte[] VoiceSocket(ReadOnlySpan<byte> bits)
    {
        Require(bits); return [.. bits[..108], .. bits[156..264]];
    }
    public static CodeResult SlotType(ReadOnlySpan<byte> bits)
    {
        Require(bits); return Codes.Golay20.Decode((Bits.UInt(bits.Slice(98,10)) << 10) | Bits.UInt(bits.Slice(156,10)));
    }
    public static CodeResult Emb(ReadOnlySpan<byte> bits)
    {
        Require(bits); return Codes.Qr16.Decode((Bits.UInt(bits.Slice(108,8)) << 8) | Bits.UInt(bits.Slice(148,8)));
    }
    public static DataBurst DecodeData(ReadOnlySpan<byte> bits)
    {
        var slot = SlotType(bits);
        byte[] air = [.. bits[..98], .. bits[166..264]];
        if (!slot.Valid) return new(-1, -1, air, false, 0, 0, 0);
        int type = (int)(slot.Data & 15), cc = (int)(slot.Data >> 4);
        if (type is 8 or 10 || type >= 11) return new(cc, type, air, false, slot.Corrections, 0, 0);
        var fec = ProductCodes.Decode196(air);
        var bytes = Bits.Pack(fec.Data); bool valid = false; int rs = 0;
        if (fec.Valid)
        {
            if (type is 1 or 2)
            {
                byte mask = type == 1 ? (byte)0x96 : (byte)0x99;
                for (int i = 9; i < 12; i++) bytes[i] ^= mask;
                valid = Checks.CorrectRs(bytes, out rs);
            }
            else if (type is 0 or 3 or 4 or 6)
            {
                uint mask = type switch { 0 => 0x6969u, 3 => 0xa5a5u, 4 => 0xaaaau, _ => 0xccccu };
                uint received = (uint)((bytes[10] << 8) | bytes[11]) ^ mask;
                valid = Checks.Crc(fec.Data.AsSpan(0,80),16,0x1021,finalXor:0xffff) == received;
            }
        }
        return new(cc, type, Bits.Unpack(bytes), valid, slot.Corrections, fec.Corrections, rs, fec.Valid);
    }
    public static byte[] MakeData(byte[] payload96, int colourCode, int type, string sync = "bs_data")
    {
        var info = ProductCodes.Encode196(payload96); var slot = Codes.Golay20.EncodeBits((uint)((colourCode << 4) | type));
        return [.. info[..98], .. slot[..10], .. Sync(sync), .. slot[10..], .. info[98..]];
    }
    public static byte[] MakeLc(byte[] lcBytes, int colourCode, bool terminator = false, string sync = "bs_data")
    {
        var parity = Checks.RsParity(lcBytes); byte mask = terminator ? (byte)0x99 : (byte)0x96;
        return MakeData(Bits.Unpack([.. lcBytes, .. parity.Select(x => (byte)(x ^ mask))]), colourCode, terminator ? 2 : 1, sync);
    }
    public static (CodeResult Tact, byte[] Payload) DecodeCach(ReadOnlySpan<byte> air)
    {
        if (air.Length != 24) throw new ArgumentException("CACH requires 24 bits.");
        uint word = 0; var payload = new List<byte>(17);
        for (int i = 0; i < 24; i++) if (Array.IndexOf(TactPositions,i) >= 0) word = (word << 1) | air[i]; else payload.Add(air[i]);
        return (Codes.Hamming7.Decode(word), payload.ToArray());
    }
    public static byte[] MakeCach(int slot, int lcss, ReadOnlySpan<byte> payload17)
    {
        if (slot is not (1 or 2) || payload17.Length != 17) throw new ArgumentException("Invalid CACH input.");
        var tact = Codes.Hamming7.EncodeBits((uint)(8 | ((slot-1)<<2) | lcss));
        var air = new byte[24]; int p = 0, t = 0;
        for (int i=0;i<24;i++) air[i] = Array.IndexOf(TactPositions,i) >= 0 ? tact[t++] : payload17[p++];
        return air;
    }
    private static void Require(ReadOnlySpan<byte> bits)
    {
        if (bits.Length != 264) throw new ArgumentException("DMR burst content requires 264 bits.");
    }
}

public sealed record DataBurst(int ColourCode, int Type, byte[] Payload, bool IntegrityValid,
    int SlotCorrections, int FecCorrections, int RsCorrectedBytes, bool FecValid = false);

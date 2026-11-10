using Dmr.Fec;
using System.Numerics;

namespace Dmr.Vocoder;

public sealed record AmbeResult(byte[] Payload, bool ChannelDecodeValid, int CorrectedBits);

/// <summary>AMBE 3600x2450 channel adapter, without speech synthesis or privacy decryption.
/// Mapping and Golay generator constants: DSD/mbelib (ISC), see THIRD_PARTY_NOTICES.md.</summary>
public static class Ambe
{
    private static readonly int[] W = [0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,2,0,2,0,2,0,2,0,2,0,2,0,2];
    private static readonly int[] X = [23,10,22,9,21,8,20,7,19,6,18,5,17,4,16,3,15,2,14,1,13,0,12,10,11,9,10,8,9,7,8,6,7,5,6,4];
    private static readonly int[] Y = [0,2,0,2,0,2,0,2,0,3,0,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3,1,3];
    private static readonly int[] Z = [5,3,4,2,3,1,2,0,1,13,0,12,22,11,21,10,20,9,19,8,18,7,17,6,16,5,15,4,14,3,13,2,12,1,11,0];
    private static readonly uint[] Generator = [0x63a,0x31d,0x7b4,0x3da,0x1ed,0x6cc,0x366,0x1b3,0x6e3,0x54b,0x49f,0x475];
    public static readonly LinearCode Golay23 = new(23, 12, 3, Generator);
    public static readonly LinearCode Golay24 = new(24, 12, 3,
        Generator.Select((p, i) => (p << 1) | (uint)((BitOperations.PopCount(p) + 1) & 1)).ToArray());
    public static AmbeResult Decode(ReadOnlySpan<byte> air)
    {
        if (air.Length != 72) throw new ArgumentException("An AMBE channel frame contains 72 bits.");
        uint[] rows = new uint[4];
        for (int i = 0; i < 36; i++)
        {
            rows[W[i]] |= (uint)air[2 * i] << X[i];
            rows[Y[i]] |= (uint)air[2 * i + 1] << Z[i];
        }
        var c0 = Golay24.Decode(rows[0]);
        uint seed = 16 * c0.Data;
        uint c1 = rows[1];
        for (int i = 22; i >= 0; i--)
        {
            seed = (173 * seed + 13849) & 65535;
            c1 ^= (seed >> 15) << i;
        }
        var d1 = Golay23.Decode(c1);
        byte[] payload = [.. Bits.FromUInt(c0.Data,12), .. Bits.FromUInt(d1.Data,12), .. Bits.FromUInt(rows[2],11), .. Bits.FromUInt(rows[3],14)];
        return new(payload, c0.Valid && d1.Valid, c0.Corrections + d1.Corrections);
    }
    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 49) throw new ArgumentException("Expected 49 AMBE parameter bits.");
        uint d0 = Bits.UInt(payload[..12]);
        uint[] rows = [Golay24.Encode(d0), Golay23.Encode(Bits.UInt(payload.Slice(12,12))), Bits.UInt(payload.Slice(24,11)), Bits.UInt(payload.Slice(35,14))];
        uint seed = 16 * d0;
        for (int i = 22; i >= 0; i--) { seed = (173 * seed + 13849) & 65535; rows[1] ^= (seed >> 15) << i; }
        var air = new byte[72];
        for (int i = 0; i < 36; i++) { air[2*i] = (byte)((rows[W[i]] >> X[i]) & 1); air[2*i+1] = (byte)((rows[Y[i]] >> Z[i]) & 1); }
        return air;
    }
}

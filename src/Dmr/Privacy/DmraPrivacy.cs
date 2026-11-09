using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Dmr.Privacy;

/// <summary>DMRA voice privacy algorithms selected by the PI header (MFID 0x10).</summary>
public enum DmraAlgorithm { Arc4, Aes128, Aes256 }

/// <summary>
/// Stateless DMRA voice privacy (ARC4, AES-128, AES-256) for deferred decryption of stored frames.
/// Frames use the packed seven-byte form: the 49 AMBE parameter bits MSB first, the low seven bits of byte 6 zero
/// (the packing of <c>ambe49_hex</c> and <c>cipher49_hex</c>). The live receiver uses the same code, so a frame
/// decrypted later with the same key, message indicator and frame index equals the live candidate.
/// Profile rules, references and limits: docs/PRIVACY.md. Keys never appear in exception messages.
/// </summary>
public static class DmraPrivacy
{
    public const int FramesPerSuperframe = 18;
    internal const int FrameBytes = 7, KeystreamBytes = FramesPerSuperframe * FrameBytes;
    private const int Arc4Drop = 256, AesBlock = 16;

    /// <summary>The DMRA algorithm for a PI header algorithm ID: (id &amp; 7) of 1, 4, 5 for IDs below 0x26
    /// (0x21/0x01 ARC4, 0x24/0x04 AES-128, 0x25/0x05 AES-256). Null for DES (2), other values, or IDs of 0x26 and above.</summary>
    public static DmraAlgorithm? AlgorithmFor(int algorithmId) =>
        algorithmId is < 0 or >= 0x26 ? null : (algorithmId & 7) switch
        {
            1 => DmraAlgorithm.Arc4,
            4 => DmraAlgorithm.Aes128,
            5 => DmraAlgorithm.Aes256,
            _ => null
        };

    /// <summary>Key length in bytes: 5 (ARC4), 16 (AES-128), 32 (AES-256).</summary>
    public static int KeyLength(DmraAlgorithm algorithm) => algorithm switch
    {
        DmraAlgorithm.Arc4 => 5,
        DmraAlgorithm.Aes128 => 16,
        DmraAlgorithm.Aes256 => 32,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm), "Unknown DMRA algorithm.")
    };

    internal static string ProfileName(DmraAlgorithm algorithm) => algorithm switch
    {
        DmraAlgorithm.Arc4 => "dmra_arc4",
        DmraAlgorithm.Aes128 => "dmra_aes128",
        _ => "dmra_aes256"
    };

    /// <summary>The message indicator of the superframe after the one using <paramref name="mi"/>.
    /// ARC4: 32 shifts of x^32+x^4+x^2+1. AES: bytes 4..7 of the 128-bit IV expansion.</summary>
    public static uint NextMessageIndicator(DmraAlgorithm algorithm, uint mi)
    {
        KeyLength(algorithm);
        if (algorithm == DmraAlgorithm.Arc4)
        {
            for (int bit = 0; bit < 32; bit++)
                mi = unchecked((mi << 1) | (((mi >> 31) ^ (mi >> 3) ^ (mi >> 1)) & 1));
            return mi;
        }
        Span<byte> iv = stackalloc byte[AesBlock];
        return ExpandIv(mi, iv);
    }

    /// <summary>18 x 7 bytes: the keystream for every frame position of the superframe that uses this MI.
    /// Frame k uses bytes 7k..7k+6; only the 49 most significant of those 56 bits are applied.</summary>
    public static void SuperframeKeystream(DmraAlgorithm algorithm, ReadOnlySpan<byte> key, uint mi, Span<byte> keystream126)
    {
        if (key.Length != KeyLength(algorithm))
            throw new ArgumentException($"A DMRA {ProfileName(algorithm)} key must contain exactly {KeyLength(algorithm)} bytes.", nameof(key));
        if (keystream126.Length != KeystreamBytes)
            throw new ArgumentException($"The superframe keystream holds exactly {KeystreamBytes} bytes.", nameof(keystream126));
        if (algorithm == DmraAlgorithm.Arc4) { Arc4Keystream(key, mi, keystream126); return; }
        using var aes = CreateAes(key);
        AesKeystream(aes, mi, keystream126);
    }

    /// <summary>True for the frames sent clear inside an encrypted superframe: the AMBE+2 silence vector
    /// (0xF801A99F8CE080, 49 bits) or bits 24..43 all zero. Only the 49 frame bits are compared.</summary>
    public static bool IsClearFiller(ReadOnlySpan<byte> packed7)
    {
        if (packed7.Length != FrameBytes) throw new ArgumentException("A packed AMBE frame holds exactly 7 bytes.", nameof(packed7));
        bool silence = packed7[0] == 0xF8 && packed7[1] == 0x01 && packed7[2] == 0xA9 && packed7[3] == 0x9F &&
                       packed7[4] == 0x8C && packed7[5] == 0xE0 && (packed7[6] & 0x80) != 0;
        return silence || (packed7[3] == 0 && packed7[4] == 0 && (packed7[5] & 0xF0) == 0);
    }

    /// <summary>Cipher to clear for frame position <paramref name="frameIndex"/> (0..17) of a superframe.
    /// Clear filler frames (<see cref="IsClearFiller"/>) are copied unchanged. The output's low seven bits of byte 6 are zero.</summary>
    public static void DecryptFrame(ReadOnlySpan<byte> keystream126, int frameIndex, ReadOnlySpan<byte> cipherPacked7, Span<byte> clearPacked7)
    {
        if (keystream126.Length != KeystreamBytes)
            throw new ArgumentException($"The superframe keystream holds exactly {KeystreamBytes} bytes.", nameof(keystream126));
        if (frameIndex is < 0 or >= FramesPerSuperframe)
            throw new ArgumentOutOfRangeException(nameof(frameIndex), "The frame index is 0 to 17.");
        if (cipherPacked7.Length != FrameBytes) throw new ArgumentException("A packed AMBE frame holds exactly 7 bytes.", nameof(cipherPacked7));
        if (clearPacked7.Length != FrameBytes) throw new ArgumentException("A packed AMBE frame holds exactly 7 bytes.", nameof(clearPacked7));
        if (IsClearFiller(cipherPacked7))
        {
            cipherPacked7.CopyTo(clearPacked7);
            clearPacked7[6] &= 0x80;
            return;
        }
        var mask = keystream126.Slice(frameIndex * FrameBytes, FrameBytes);
        for (int n = 0; n < FrameBytes; n++) clearPacked7[n] = (byte)(cipherPacked7[n] ^ mask[n]);
        clearPacked7[6] &= 0x80;
    }

    // ARC4(key || MI big-endian), first 256 bytes discarded, then 7 bytes per frame.
    internal static void Arc4Keystream(ReadOnlySpan<byte> key5, uint mi, Span<byte> keystream126)
    {
        Span<byte> material = stackalloc byte[9];
        key5.CopyTo(material);
        BinaryPrimitives.WriteUInt32BigEndian(material[5..], mi);
        Arc4.Keystream(material, Arc4Drop, keystream126);
        material.Clear();
    }

    internal static Aes CreateAes(ReadOnlySpan<byte> key)
    {
        var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes;
    }

    // IV from the 32-bit MI, AES-OFB, first output block discarded, then 7 bytes per frame.
    internal static void AesKeystream(Aes aes, uint mi, Span<byte> keystream126)
    {
        Span<byte> iv = stackalloc byte[AesBlock];
        ExpandIv(mi, iv);
        // One discarded block plus 126 bytes: nine blocks (the reference computes ten; the tenth is never used).
        Span<byte> output = stackalloc byte[9 * AesBlock];
        AesOfb(aes, iv, output);
        output.Slice(AesBlock, KeystreamBytes).CopyTo(keystream126);
        output.Clear();
    }

    /// <summary>OFB keystream: block 0 = AES_k(IV), block i = AES_k(block i-1).</summary>
    internal static void AesOfb(Aes aes, ReadOnlySpan<byte> iv, Span<byte> output)
    {
        if (iv.Length != AesBlock || output.Length % AesBlock != 0) throw new ArgumentException("Invalid OFB block sizes.");
        ReadOnlySpan<byte> feedback = iv;
        for (int offset = 0; offset < output.Length; offset += AesBlock)
        {
            var block = output.Slice(offset, AesBlock);
            aes.EncryptEcb(feedback, block, PaddingMode.None);
            feedback = block;
        }
    }

    /// <summary>
    /// Expands the 32-bit MI into the 128-bit AES IV: bytes 0..3 are the MI big-endian; bytes 4..15 are 96 further
    /// output bits of the LFSR x^32+x^22+x^2+x+1 (taps 31, 21, 1, 0) started at the MI, packed MSB first.
    /// Returns bytes 4..7 big-endian, which is the next superframe's MI.
    /// </summary>
    internal static uint ExpandIv(uint mi, Span<byte> iv16)
    {
        BinaryPrimitives.WriteUInt32BigEndian(iv16, mi);
        iv16[4..].Clear();
        uint lfsr = mi;
        for (int n = 0; n < 96; n++)
        {
            uint bit = ((lfsr >> 31) ^ (lfsr >> 21) ^ (lfsr >> 1) ^ lfsr) & 1;
            lfsr = unchecked((lfsr << 1) | bit);
            int x = 32 + n;
            iv16[x / 8] = (byte)((iv16[x / 8] << 1) | (int)bit);
        }
        return BinaryPrimitives.ReadUInt32BigEndian(iv16[4..]);
    }

    /// <summary>Packs 49 unpacked bits into the seven-byte form (low seven bits of byte 6 zero).</summary>
    internal static void Pack49(ReadOnlySpan<byte> bits49, Span<byte> packed7)
    {
        if (bits49.Length != 49) throw new ArgumentException("Expected 49 unpacked AMBE bits.");
        packed7[..FrameBytes].Clear();
        for (int i = 0; i < 49; i++) packed7[i >> 3] |= (byte)((bits49[i] & 1) << (7 - (i & 7)));
    }
}

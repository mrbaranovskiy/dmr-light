using System.Buffers.Binary;

namespace Dmr.Privacy;

// DMRA MFID 0x10, algorithm 0x21 (or compatible 0x01), PI-header acquisition.
// Profile references and validation limits: docs/PRIVACY.md.
internal sealed class DmraArc4Context
{
    private readonly byte[]? key;
    private Arc4? stream;
    private bool awaitingVoiceSync = true;
    public byte KeyId { get; }
    public uint MessageIndicator { get; private set; }
    public int FrameIndex { get; private set; }
    public bool Synchronized { get; private set; } = true;
    public string Status => !Synchronized ? "alignment_lost" : key == null ? "missing_key" : "ready";

    public DmraArc4Context(byte keyId, uint messageIndicator, Arc4Keyring? keys)
    {
        KeyId = keyId; MessageIndicator = messageIndicator; key = keys?.Find(keyId);
        InitializeStream();
    }

    private void InitializeStream()
    {
        if (key == null) return;
        Span<byte> material = stackalloc byte[9];
        key.CopyTo(material);
        BinaryPrimitives.WriteUInt32BigEndian(material[5..], MessageIndicator);
        stream = new(material);
        stream.Skip(256);
    }

    public void AlignBurst(int phase, bool voiceSync)
    {
        if ((awaitingVoiceSync && !voiceSync) || phase != FrameIndex / 3)
        { Synchronized = false; stream = null; }
        awaitingVoiceSync = false;
    }

    public byte[]? Transform(ReadOnlySpan<byte> cipher49, bool channelValid)
    {
        if (cipher49.Length != 49) throw new ArgumentException("Expected 49 unpacked AMBE bits.");
        byte[]? clear = null;
        if (Synchronized && stream != null)
        {
            if (channelValid)
            {
                clear = new byte[49];
                for (int octet = 0; octet < 7; octet++)
                {
                    byte mask = stream.Next();
                    for (int bit = 0; bit < 8 && octet * 8 + bit < 49; bit++)
                        clear[octet * 8 + bit] = (byte)(cipher49[octet * 8 + bit] ^ ((mask >> (7 - bit)) & 1));
                }
            }
            else stream.Skip(7);
        }
        AdvanceFrame();
        return clear;
    }

    public void SkipFrame()
    {
        if (Synchronized) stream?.Skip(7);
        AdvanceFrame();
    }

    private void AdvanceFrame()
    {
        if (!Synchronized || ++FrameIndex != 18) return;
        FrameIndex = 0;
        MessageIndicator = NextMessageIndicator(MessageIndicator);
        InitializeStream();
    }

    internal static uint NextMessageIndicator(uint value)
    {
        for (int bit = 0; bit < 32; bit++)
            value = unchecked((value << 1) | (((value >> 31) ^ (value >> 3) ^ (value >> 1)) & 1));
        return value;
    }
}

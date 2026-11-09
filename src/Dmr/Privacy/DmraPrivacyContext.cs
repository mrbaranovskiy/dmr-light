using System.Security.Cryptography;

namespace Dmr.Privacy;

// DMRA MFID 0x10 privacy state of one voice track, acquired from a PI header: ARC4, AES-128 or AES-256.
// Alignment, the frame position 0..17 and the superframe MI are tracked with or without a key, so stored
// ciphertext can be decrypted later (DmraPrivacy). With a key, the per-superframe keystream is computed once
// per superframe through the same DmraPrivacy code the deferred API uses. Profile rules: docs/PRIVACY.md.
internal sealed class DmraPrivacyContext : IDisposable
{
    private readonly byte[]? key;           // ARC4 key copy; never emitted
    private readonly Aes? aes;              // AES key schedule, created once per PI header
    private readonly byte[]? keystream;     // current superframe, 18 x 7 bytes, only with a key
    private bool awaitingVoiceSync = true;
    public DmraAlgorithm Algorithm { get; }
    public byte KeyId { get; }
    /// <summary>MI of the current superframe.</summary>
    public uint MessageIndicator { get; private set; }
    /// <summary>Position 0..17 of the next frame in the superframe.</summary>
    public int FrameIndex { get; private set; }
    public bool Synchronized { get; private set; } = true;
    public bool HasKey => keystream != null;
    public string Status => !Synchronized ? "alignment_lost" : keystream == null ? "missing_key" : "ready";

    public DmraPrivacyContext(DmraAlgorithm algorithm, byte keyId, uint messageIndicator, PrivacyKeyring? keys)
    {
        Algorithm = algorithm; KeyId = keyId; MessageIndicator = messageIndicator;
        var material = keys?.Find(algorithm, keyId);
        if (material == null) return;
        keystream = new byte[DmraPrivacy.KeystreamBytes];
        if (algorithm == DmraAlgorithm.Arc4) key = material;
        else { aes = DmraPrivacy.CreateAes(material); Array.Clear(material); }
        FillKeystream();
    }

    private void FillKeystream()
    {
        if (keystream == null) return;
        if (aes != null) DmraPrivacy.AesKeystream(aes, MessageIndicator, keystream);
        else DmraPrivacy.Arc4Keystream(key!, MessageIndicator, keystream);
    }

    public void AlignBurst(int phase, bool voiceSync)
    {
        if ((awaitingVoiceSync && !voiceSync) || phase != FrameIndex / 3) Synchronized = false;
        awaitingVoiceSync = false;
    }

    /// <summary>Consumes one frame position. Returns true and writes the candidate when aligned, keyed and channel-valid.</summary>
    public bool Transform(ReadOnlySpan<byte> cipherPacked7, bool channelValid, Span<byte> clearPacked7)
    {
        bool produced = false;
        if (Synchronized && keystream != null && channelValid)
        {
            DmraPrivacy.DecryptFrame(keystream, FrameIndex, cipherPacked7, clearPacked7);
            produced = true;
        }
        AdvanceFrame();
        return produced;
    }

    /// <summary>Test convenience: unpacked 49 bits in, packed candidate (7 bytes) or null out.</summary>
    internal byte[]? Transform(ReadOnlySpan<byte> cipher49, bool channelValid)
    {
        Span<byte> cipher = stackalloc byte[DmraPrivacy.FrameBytes];
        DmraPrivacy.Pack49(cipher49, cipher);
        var clear = new byte[DmraPrivacy.FrameBytes];
        return Transform(cipher, channelValid, clear) ? clear : null;
    }

    public void SkipFrame() => AdvanceFrame();

    private void AdvanceFrame()
    {
        if (!Synchronized || ++FrameIndex != DmraPrivacy.FramesPerSuperframe) return;
        FrameIndex = 0;
        MessageIndicator = DmraPrivacy.NextMessageIndicator(Algorithm, MessageIndicator);
        FillKeystream();
    }

    public void Dispose() => aes?.Dispose();
}

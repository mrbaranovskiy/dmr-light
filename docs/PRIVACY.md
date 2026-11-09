# DMR privacy decoding

The receiver supports **known-key DMRA voice privacy** with three algorithms: **ARC4 (RC4)**, **AES-128** and **AES-256**. It acquires the privacy parameters from an integrity-valid PI header with manufacturer ID `0x10`. Without a key it still tracks the superframe position and message indicator (MI), so stored ciphertext can be decrypted later with the stateless `DmraPrivacy` API. Other manufacturer profiles and DES remain opaque.

## Supported profiles

PI header (MFID `0x10`): byte 0 is the algorithm ID, byte 2 the key ID, bytes 3..6 the 32-bit MI (big-endian). Only algorithm IDs below `0x26` are interpreted. The algorithm is selected by `id & 7`, as in the reference; bit `0x20` set is "DMRA", clear is "DMRA compatible" (AnyTone radios send `0x01`):

| `id & 7` | Typical IDs | `privacy_profile` | Key | Status |
|---|---|---|---|---|
| 1 | `0x21`, `0x01` | `dmra_arc4` | 5 bytes | implemented |
| 4 | `0x24`, `0x04` | `dmra_aes128` | 16 bytes | implemented |
| 5 | `0x25`, `0x05` | `dmra_aes256` | 32 bytes | implemented |
| 2 | `0x22`, `0x02` | `dmra_des` | - | `unsupported_profile` |
| other, or ID >= `0x26` | | null | - | `unsupported_profile` |

Because the mapping uses `id & 7`, the IDs `0x09`, `0x11` and `0x19` also select ARC4 (and `0x0C`/`0x14`/`0x1C` AES-128, `0x0D`/`0x15`/`0x1D` AES-256), exactly as the reference does. Earlier versions of this receiver accepted only `0x21` and `0x01`.

## CLI and API

Create a local key file, for example `keys.local.json`. This example contains synthetic test keys; replace them with the radio's configured values:

```json
{
  "dmra_arc4": [
    { "key_id": 7, "key_hex": "0102030405" }
  ],
  "dmra_aes": [
    { "key_id": 1, "key_hex": "000102030405060708090A0B0C0D0E0F" },
    { "key_id": 2, "key_hex": "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F" }
  ]
}
```

```powershell
dotnet run --project src/Dmr.Cli -c Release -- capture.wav --offset -12500 --privacy-keys keys.local.json --output output/capture.jsonl
```

Both sections are optional. Key IDs are decimal integers from 0 to 255. ARC4 keys are exactly ten hexadecimal digits; AES keys are 32 (AES-128) or 64 (AES-256) digits, including leading zeroes. A key ID is unique per algorithm: one ID may hold both an AES-128 and an AES-256 key, and a PI header selects by both algorithm and key ID (an AES-128 header never uses a 32-byte key). Duplicate IDs, unknown sections and malformed entries are rejected. An all-zero key is valid and is distinct from a missing key. The keyring applies to one receiver/carrier; both slots select independently by key ID. Systems reusing the same key ID for different keys need separate receiver configurations.

For the streaming API:

```csharp
using Dmr;
using Dmr.Privacy;

var keys = PrivacyKeyring.FromJson(File.ReadAllText("keys.local.json"));
// or: new PrivacyKeyring(arc4: ..., aes128: ..., aes256: ...), each an IReadOnlyDictionary<byte, byte[]> by key ID
var receiver = new DmrReceiver(
    new ReceiverOptions(SampleRate: 48000, PrivacyKeys: keys),
    ev => Console.WriteLine(ev.ToJson()));
```

`Arc4Keyring` (ARC4 keys only, `{"dmra_arc4":[...]}`) remains available and is a `PrivacyKeyring`, so existing callers compile unchanged. Keyrings copy supplied key arrays and are immutable. Key values are excluded from output events and from key-format and argument error messages. `keys.local.json` and `*.keys.local.json` are ignored by the repository's ignore rules. The application keeps keys in managed memory (and, for AES, in the platform's key handle) for the lifetime of its keyring/receiver; it does not promise secure memory erasure.

## Pipeline and state

The voice path is:

```text
72 received air bits -> AMBE channel decode -> 49 ciphertext bits (cipher49_hex)
                    -> DMRA keystream for (MI, frame position) -> 49 candidate clear bits (ambe49_hex)
```

AMBE channel descrambling is distinct from traffic decryption. `payload_hex` always retains the original nine received bytes. Frames use the seven-byte, MSB-first packing of `ambe49_hex`, with seven zero padding bits at the end.

A superframe is 18 vocoder frames (bursts A-F, three frames each). Each superframe has its own 32-bit MI and 126-byte keystream; frame `k` (0..17) XORs its 49 bits with the 49 most significant bits of keystream bytes `7k .. 7k+6`, and the remaining seven keystream bits are skipped.

**ARC4** (`dmra_arc4`):

- Initialize ARC4 with the five key bytes followed by the MI (big-endian), discard the first 256 keystream bytes, then take 126 bytes.
- Next superframe's MI: 32 shifts of the 32-bit MI with feedback `(mi >> 31) ^ (mi >> 3) ^ (mi >> 1)` (x^32 + x^4 + x^2 + 1).

**AES-128 / AES-256** (`dmra_aes128`, `dmra_aes256`):

- Expand the MI to a 128-bit IV: bytes 0..3 are the MI (big-endian); bytes 4..15 are 96 further output bits, packed MSB first, of the 32-bit LFSR started at the MI with `bit = ((l>>31) ^ (l>>21) ^ (l>>1) ^ l) & 1; l = (l << 1) | bit` (taps 32, 22, 2, 1).
- Keystream: AES-OFB with that IV (output block 0 = AES_k(IV), block i = AES_k(block i-1)). Block 0 (16 bytes) is discarded; the next 126 bytes are the superframe keystream.
- Next superframe's MI: IV bytes 4..7 (big-endian), i.e. the LFSR state after 32 shifts.
- AES is the platform implementation (`System.Security.Cryptography.Aes`, ECB on the feedback block); the receiver contains no AES code of its own.

**Clear filler frames** (all three algorithms): a frame whose received 49 bits equal the AMBE+2 silence vector (`F801A99F8CE080`, the 49 most significant bits of that 56-bit value) or whose bits 24..43 are all zero is sent clear. It is not XORed (the candidate equals the received bits) but its keystream position is still consumed. The reference applies this rule to both ARC4 and AES; for ARC4 this is new in this version. A clear frame that happens to match these patterns after encryption is therefore indistinguishable from filler; with random keystream the probability is about 2^-20 per frame.

Each relative track owns its own privacy context, MI and frame counter, even when the absolute slot number is unknown. The context exists for every supported profile with or without a key. Known burst erasures and failed AMBE channel decoding consume the corresponding keystream positions. Unexpected superframe phase suspends the context (position and MI are no longer reported) until another valid PI header establishes alignment. Call/session termination and receiver discontinuities clear the context.

The first burst after initial PI acquisition must be voice sync A. Receiving a later burst without that anchor cannot establish a position safely. There is no late-entry MI recovery or manual MI override yet. Unknown gaps require `DmrReceiver.Discontinuity`, as for the rest of the receiver.

## Deferred decryption (`DmraPrivacy`)

An application that stores encrypted frames can decrypt them later, trying any number of keys, without a receiver and without knowing the key ID. Store, per vocoder frame, `cipher49_hex`, `privacy_message_indicator`, `privacy_frame_index` and `privacy_algorithm_id` (and `privacy_key_id` to narrow the search):

```csharp
public enum DmraAlgorithm { Arc4, Aes128, Aes256 }
public static class DmraPrivacy
{
    public const int FramesPerSuperframe = 18;
    public static DmraAlgorithm? AlgorithmFor(int algorithmId);   // id & 7 mapping above; null for DES, others, >= 0x26
    public static int KeyLength(DmraAlgorithm algorithm);         // 5, 16, 32
    public static uint NextMessageIndicator(DmraAlgorithm algorithm, uint mi);
    public static void SuperframeKeystream(DmraAlgorithm algorithm, ReadOnlySpan<byte> key, uint mi, Span<byte> keystream126);
    public static bool IsClearFiller(ReadOnlySpan<byte> packed7);
    public static void DecryptFrame(ReadOnlySpan<byte> keystream126, int frameIndex, ReadOnlySpan<byte> cipherPacked7, Span<byte> clearPacked7);
}
```

```csharp
var algorithm = DmraPrivacy.AlgorithmFor(algorithmId) ?? throw new NotSupportedException();
var keystream = new byte[126];
DmraPrivacy.SuperframeKeystream(algorithm, key, mi, keystream);   // once per (key, MI)
var clear = new byte[7];
DmraPrivacy.DecryptFrame(keystream, frameIndex, Convert.FromHexString(cipher49Hex), clear);
```

The live receiver uses these functions for its own decryption, so for the same key, MI and frame index the deferred result is byte-identical to the live `ambe49_hex` (tested with ARC4, AES-128 and AES-256, and on the radio capture below). Frames reported with `privacy_frame_index` null (alignment unknown) cannot be placed in a keystream. Wrong argument sizes raise `ArgumentException`/`ArgumentOutOfRangeException`; messages never contain key bytes. `SuperframeKeystream` creates an AES key schedule per call; cache the keystream per (key, MI) when trying many frames.

There is no voice authentication tag: a wrong key also yields a candidate. Choosing the right key requires the caller's own scoring (for example AMBE parameter statistics or speech-likeness).

## Output and limits

PI-derived `call_update` events include `privacy_fid`, `privacy_algorithm_id`, `privacy_key_id`, `privacy_message_indicator` (the header MI), `privacy_profile` and `decryption_status`. Algorithm, key ID and MI interpretation applies only to the recognized `0x10` PI layout. The raw header is always preserved.

Each `vocoder_frame` carries:

| Field | Value |
|---|---|
| `privacy_profile`, `privacy_fid`, `privacy_algorithm_id`, `privacy_key_id` | From the last valid PI header of the call; null without one |
| `privacy_message_indicator` | MI (`X8`) of the frame's superframe while a supported profile is aligned, with or without a key; else null |
| `privacy_frame_index` | Frame position 0..17 in the superframe while aligned; else null |
| `cipher49_hex` | When privacy is indicated and the AMBE channel decode is valid: the FEC-corrected received 49 bits, packed like `ambe49_hex`; else null |
| `ambe49_hex` | The decrypted candidate when decrypted (or clear bits for non-private standard calls); else null |

Example: an encrypted frame without a configured key (synthetic AES-256 test traffic, MI `00000001`):

```json
{"schema_version":1,"sequence":15,"type":"vocoder_frame","capture_id":"capture","rf_sample_index":8810,"rf_time_seconds":0.18354166666666666,"track":0,"slot":1,"session_id":"capture:1","data":{"burst_id":5,"superframe_phase":"A","frame_index_in_burst":0,"frame_sequence":0,"playout_offset_ms":0,"duration_ms":20,"representation":"dmr_voice72_air","codec_profile":"ambe_3600x2450_v1","bit_length":72,"payload_hex":"0764D92E9F2F01C685","privacy_status":"indicated","metadata_revision":2,"colour_code":7,"source_id":123456,"destination_id":91,"channel_decode_valid":true,"corrected_bits":0,"voice_crc_available":false,"erasure":false,"ambe49_hex":null,"ambe49_status":"opaque_privacy","privacy_profile":"dmra_aes256","privacy_fid":16,"privacy_algorithm_id":37,"privacy_key_id":9,"privacy_message_indicator":"00000001","decryption_status":"missing_key","privacy_frame_index":0,"cipher49_hex":"0DD8A98899A880"}}
```

| `decryption_status` | Meaning |
|---|---|
| `ready` | PI metadata only: key and initial parameters are available; awaiting voice alignment |
| `decrypted_unverified` | The keystream was applied (or the frame is clear filler); `ambe49_hex` contains a candidate |
| `not_required` | Privacy is not indicated; normal codec/profile checks still apply |
| `missing_parameters` | No usable PI context, including encrypted late entry |
| `missing_key` | Supported PI profile, but no configured key matches its algorithm and ID; position and MI are still reported |
| `unsupported_profile` | Valid PI header uses an unimplemented manufacturer/algorithm profile (DES, others) |
| `alignment_lost` | Observed burst phase does not match the keystream position |
| `invalid_privacy_header` | A corrupt replacement PI header invalidated the prior context |
| `channel_decode_failed` | Keystream position was consumed but no candidate was emitted |

`privacy_status` continues to describe on-air signalling; successful decryption does not change it to `not_indicated`. `ambe49_status` is `decrypted_unverified` when a candidate is emitted. There is no voice authentication tag or end-to-end voice CRC here: a wrong key can produce a candidate and cannot be identified as wrong from successful FEC. The receiver does not synthesize speech.

Validation:

- ARC4: RFC 6229 known answers; 108 synthetic parameter-frame vectors generated by separately compiled pinned DSD-FME C functions; synthetic encrypted IQ through the full receiver; and a **real AnyTone radio capture** (DMRA-compatible ARC4, algorithm ID `0x01`): with the key all 234 frames decrypt to intelligible speech, and the deferred API reproduces every live frame. The capture is the fixture `tests/fixtures/arc4_anytone_1488to1.cs16`, published with its test key `0000002806` by the radio's owner (see [VALIDATION.md](VALIDATION.md)). That capture contains no clear filler frames, so the filler rule is covered by synthetic tests only.
- AES: NIST SP 800-38A OFB-AES128/OFB-AES256 known answers and a CFB-of-zeros composition through the OFB code; the IV expansion against an independent bit-stream reimplementation and a hand-checked IV (`00000001` expands to `00000001B6DB68A3CF213212B93A9645`); superframe keystreams against an independent composition; synthetic AES-128 and AES-256 IQ through the full receiver (live, deferred and wrong-key). **No radio-captured AES vector exists yet**; AnyTone/Baofeng AES captures with a known key are the validation gate for the AES profiles.

Not implemented: late-entry MI recovery (the reference replaces the LFSR-derived MI with a CRC-valid embedded late-entry MI when they differ; this receiver always uses the LFSR-derived MI), DES, Hytera enhanced/basic privacy, Kirisun, Motorola/AnyTone/TYT basic privacy and other vendor profiles. The reference also leaves an ARC4 frame undecrypted when its first Golay word has three or more errors; this receiver emits no candidate only when channel decoding fails.

The supplied `dmr_test.wav` still exports 216 opaque frames. Its observed LC FID is `0x68`; this is insufficient to select a cipher or infer a key. No valid supported PI header has been recovered from it.

Additional privacy methods should be separate profile adapters: PI parsing, cipher setup, voice-bit packing, MI evolution and reset rules all need their own vectors.

## References

- [RFC 6229: ARC4 known-answer vectors](https://www.rfc-editor.org/rfc/rfc6229.html); [NIST SP 800-38A](https://csrc.nist.gov/pubs/sp/800/38/a/final), F.4.1 and F.4.5 (OFB-AES).
- DSD-FME commit `fa4a33258209d2b02c1b9331340869f189582740`: [PI parsing, MI evolution and AES IV expansion (`LFSR128d`)](https://github.com/lwvmobile/dsd-fme/blob/fa4a33258209d2b02c1b9331340869f189582740/src/dmr_pi.c), [voice-bit packing, keystream application and clear filler handling](https://github.com/lwvmobile/dsd-fme/blob/fa4a33258209d2b02c1b9331340869f189582740/src/dsd_mbe.c), [superframe refresh](https://github.com/lwvmobile/dsd-fme/blob/fa4a33258209d2b02c1b9331340869f189582740/src/dmr_le.c), [refresh order in the burst loop](https://github.com/lwvmobile/dsd-fme/blob/fa4a33258209d2b02c1b9331340869f189582740/src/dmr_bs.c), [AES-OFB keystream output](https://github.com/lwvmobile/dsd-fme/blob/fa4a33258209d2b02c1b9331340869f189582740/src/crypt-aes.c) and [ARC4 reference](https://github.com/lwvmobile/dsd-fme/blob/fa4a33258209d2b02c1b9331340869f189582740/src/crypt-rc4.c).

The profile implementation follows these interoperability references; it is not a claim that every vendor's enhanced privacy uses the same rules. See [third-party notices](../THIRD_PARTY_NOTICES.md). Regenerate the ARC4 fixture with `python tools/make_arc4_vectors.py` (development-only Python, GCC and network access).

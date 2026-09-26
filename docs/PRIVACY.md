# DMR privacy decoding

The receiver supports **known-key DMRA ARC4 (RC4)** voice decoding. It acquires the privacy parameters from an integrity-valid PI header with manufacturer ID `0x10` and algorithm ID `0x21` or compatible `0x01`. The configured key is five bytes (40 bits), selected by the header's one-byte key ID. Other algorithms and manufacturer profiles remain opaque.

## CLI and API

Create a local key file, for example `keys.local.json`. This example contains a synthetic test key; replace it with the radio's configured value:

```json
{
  "dmra_arc4": [
    { "key_id": 7, "key_hex": "0102030405" }
  ]
}
```

```powershell
dotnet run --project src/Dmr.Cli -c Release -- capture.wav --offset -12500 --privacy-keys keys.local.json --output output/capture.jsonl
```

Key IDs are decimal integers from 0 to 255; keys are exactly ten hexadecimal digits, including leading zeroes. Duplicate IDs and malformed entries are rejected. An all-zero key is valid and is distinct from a missing key. The keyring applies to one receiver/carrier; both slots select independently by key ID. Systems reusing the same key ID for different keys need separate receiver configurations.

For the streaming API:

```csharp
using Dmr;
using Dmr.Privacy;

var keys = Arc4Keyring.FromJson(File.ReadAllText("keys.local.json"));
var receiver = new DmrReceiver(
    new ReceiverOptions(SampleRate: 48000, PrivacyKeys: keys),
    ev => Console.WriteLine(ev.ToJson()));
```

The keyring copies supplied key arrays and is immutable. Key values are excluded from output events and key-format error messages. `keys.local.json` and `*.keys.local.json` are ignored by the repository's ignore rules. The application keeps keys in managed memory for the lifetime of its keyring/receiver; it does not promise secure memory erasure.

## Pipeline and state

The voice path is:

```text
72 received air bits -> AMBE channel decode -> 49 ciphertext bits
                    -> selected privacy adapter -> 49 candidate clear bits
```

AMBE channel descrambling is distinct from traffic decryption. `payload_hex` always retains the original nine received bytes. With a recognized profile, matching key and known alignment, `ambe49_hex` contains the decrypted candidate in the existing seven-byte, MSB-first packing, with seven zero padding bits at the end.

The DMRA adapter uses these profile-specific rules:

- Concatenate the five key bytes with the four-byte message indicator, in big-endian order.
- Initialize ARC4 and discard the first 256 keystream bytes.
- Consume seven bytes for each 49-bit frame. XOR the first 49 bits; skip the remaining seven keystream bits.
- After eighteen frames (bursts A–F), advance the 32-bit MI by 32 shifts using feedback `(mi >> 31) ^ (mi >> 3) ^ (mi >> 1)`, retaining its low bit at each shift. Reinitialize the next superframe's cipher and discard 256 bytes again.

Each relative track owns its own cipher, MI and frame counter, even when the absolute slot number is unknown. Known burst erasures and failed AMBE channel decoding consume the corresponding keystream positions. Unexpected superframe phase suspends decryption until another valid PI header establishes alignment. Call/session termination and receiver discontinuities clear the context.

The first burst after initial PI acquisition must be voice sync A. Receiving a later burst without that anchor cannot establish a position safely. There is no late-entry MI recovery or manual MI override yet. Unknown gaps require `DmrReceiver.Discontinuity`, as for the rest of the receiver.

## Output and limits

PI-derived `call_update` events include `privacy_fid`, `privacy_algorithm_id`, `privacy_key_id`, `privacy_message_indicator`, `privacy_profile` and `decryption_status`. Algorithm, key ID and MI interpretation currently applies only to the recognized `0x10` PI layout. The raw header is always preserved. Each voice frame repeats the selected profile/IDs and current MI when alignment is known.

| `decryption_status` | Meaning |
|---|---|
| `ready` | PI metadata only: key and initial parameters are available; awaiting voice alignment |
| `decrypted_unverified` | ARC4 was applied; `ambe49_hex` contains a candidate |
| `not_required` | Privacy is not indicated; normal codec/profile checks still apply |
| `missing_parameters` | No usable PI context, including encrypted late entry |
| `missing_key` | Supported PI profile, but no configured key matches its ID |
| `unsupported_profile` | Valid PI header uses an unimplemented manufacturer/algorithm profile |
| `alignment_lost` | Observed burst phase does not match the keystream position |
| `invalid_privacy_header` | A corrupt replacement PI header invalidated the prior context |
| `channel_decode_failed` | Keystream position was consumed but no candidate was emitted |

`privacy_status` continues to describe on-air signalling; successful decryption does not change it to `not_indicated`. `ambe49_status` is `decrypted_unverified` when a candidate is emitted. There is no voice authentication tag or end-to-end voice CRC here: a wrong key can produce a candidate and cannot be identified as wrong from successful FEC. The receiver does not synthesize speech.

The implementation is checked against RFC 6229 ARC4 vectors, 108 synthetic parameter-frame vectors generated by separately compiled pinned DSD-FME C functions, and synthetic encrypted IQ through the full receiver. An independently labelled compatible radio capture with a known key remains a validation gate. Manufacturer-specific clear silence/preemption handling and embedded late-entry privacy signalling are not implemented.

The supplied `dmr_test.wav` still exports 216 opaque frames. Its observed LC FID is `0x68`; this is insufficient to select a cipher or infer a key. No valid supported PI header has been recovered from it. Its key and vendor/profile details are needed before an appropriate adapter can be validated against that recording.

Additional privacy methods should be separate profile adapters: PI parsing, cipher setup, voice-bit packing, MI evolution and reset rules all need their own vectors. AES and vendor-specific ARC4/basic privacy are not implemented by this change.

## References

- [RFC 6229: ARC4 known-answer vectors](https://www.rfc-editor.org/rfc/rfc6229.html).
- DSD-FME commit `fa4a33258209d2b02c1b9331340869f189582740`: [PI parsing and MI evolution](https://github.com/lwvmobile/dsd-fme/blob/fa4a33258209d2b02c1b9331340869f189582740/src/dmr_pi.c), [voice-byte packing and key/MI construction](https://github.com/lwvmobile/dsd-fme/blob/fa4a33258209d2b02c1b9331340869f189582740/src/dsd_mbe.c), [superframe reset](https://github.com/lwvmobile/dsd-fme/blob/fa4a33258209d2b02c1b9331340869f189582740/src/dmr_le.c), and [ARC4 reference](https://github.com/lwvmobile/dsd-fme/blob/fa4a33258209d2b02c1b9331340869f189582740/src/crypt-rc4.c).

The profile implementation follows these interoperability references; it is not a claim that every vendor's enhanced privacy uses the same rules. See [third-party notices](../THIRD_PARTY_NOTICES.md). Regenerate the fixture with `python tools/make_arc4_vectors.py` (development-only Python, GCC and network access).

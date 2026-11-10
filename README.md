# DMR IQ receiver

A managed C#/.NET 8 receiver that converts recorded or incrementally supplied IQ into DMR signalling events and vocoder frames. It decodes one configured RF channel, including both TDMA slots, with optional known-key DMRA ARC4, AES-128 and AES-256 voice decryption and a stateless API for decrypting stored frames later. Optional WAV export uses an externally supplied mbelib speech decoder.

## Build and run

Requires the .NET 8 SDK (8.0.300 or a later 8.0 feature band, selected by `global.json`). Normal event extraction has no runtime NuGet packages, native libraries, FFTW dependencies, or network calls. WAV export additionally requires a user-supplied mbelib library.

```powershell
dotnet build Dmr.sln -c Release
dotnet run --project src/Dmr.Cli -c Release -- dmr_test.wav --offset -12500 --output output/dmr_test.jsonl
dotnet run --project tests/Dmr.Tests -c Release
```

To publish a Windows executable that uses the installed .NET 8 runtime:

```powershell
dotnet publish src/Dmr.Cli -c Release -r win-x64 --self-contained false -o output/publish
./output/publish/Dmr.Cli.exe dmr_test.wav --offset -12500 --output output/dmr_test.jsonl
```

Raw input examples:

```powershell
dotnet run --project src/Dmr.Cli -c Release -- capture.cf32 --format cf32 --sample-rate 96000 --offset 12500 --output output/capture.jsonl
dotnet run --project src/Dmr.Cli -c Release -- capture.cs16 --format cs16 --sample-rate 48000 --output output/capture.jsonl
```

For clear standard-profile voice, supply a native [mbelib 1.3.0](https://github.com/szechyjs/mbelib) library and request WAV files:

```powershell
dotnet run --project src/Dmr.Cli -c Release -- clear_capture.wav --offset -12500 --output output/clear.jsonl --wav-dir output/audio --mbelib C:\path\to\mbelib.dll
```

The library is loaded only when WAV export is requested. WAV files are mono 16-bit PCM at 8 kHz, one per call and track. Missing or ineligible frames within an active WAV are silent for 20 ms; calls with no eligible frames produce no WAV. File names include the capture and session IDs. On Linux or macOS, pass the path to the corresponding shared library. The native library and its dependencies must match the process architecture. The receiver does not download or ship mbelib.

`--offset` is the selected carrier's frequency relative to IQ centre, in Hz; positive means above centre. It is required when the signal is off-centre. Automatic channel discovery is not implemented. `--centre-frequency` optionally records the capture's absolute centre frequency. `--capture-time` records an ISO 8601 start time. DSP polarity is resolved automatically using validated signalling; `--polarity normal|inverted` constrains that decision. `--conjugate` conjugates IQ before frequency translation.

Input rates from 24 to 384 ksample/s are accepted. Channelize wider SDR streams before this receiver. WAV input must contain two channels, I then Q, with PCM16/24/32 or float32 samples. Raw input supports interleaved little-endian `cf32`, `cs16`, `cs32`, or unsigned `cu8`. Discriminator-audio WAV is not IQ.

`--chunk-size` changes input buffering, without changing output. `--symbols` includes per-burst symbol values, hard dibits, squared-distance costs for `00,01,10,11`, residual frequency error, and uncalibrated relative power. These costs are not calibrated log likelihoods.

`--privacy-keys keys.local.json` enables known-key DMRA ARC4, AES-128 and AES-256 decoding when a supported, valid PI header supplies the algorithm, key ID and message indicator. See [privacy setup, supported profiles and validation limits](docs/PRIVACY.md). The default requires no key file and preserves encrypted voice frames; their corrected ciphertext, superframe position and message indicator are still emitted so they can be decrypted later with `Dmr.Privacy.DmraPrivacy`.

Combine `--privacy-keys` with `--wav-dir` and `--mbelib` to export decrypted voice candidates as WAV. These candidates remain unverified: an incorrect key cannot be detected from channel correction success.

## Supplied recording

`dmr_test.wav` contains 635,136 stereo PCM32 IQ samples at 54,688 Hz, approximately 11.614 seconds. At offset -12,500 Hz the current receiver extracts:

- 72 voice bursts / **216 raw 72-bit vocoder frames**.
- Five valid voice LC headers, twelve valid embedded LC messages, and a valid terminator.
- Colour code 15; mobile-station sync; absolute slot number unavailable.
- Feature ID `0x68`, FLCO `0`, and raw LC `0068400044C000418C`.

The user identifies this capture as encrypted. Its manufacturer-specific LC is preserved as opaque: the receiver does not assume that its source/destination field layout is the standardized layout. Source and destination IDs remain null, privacy status is `unknown_vendor`, and `ambe49_hex` is null. WAV export produces no audio files from this capture. The raw channel frames remain available for a downstream vendor/privacy adapter. The file is a regression fixture, not an independently labelled RF conformance recording.

## Streaming API

Reference [src/Dmr/Dmr.csproj](src/Dmr/Dmr.csproj):

```csharp
using System.Numerics;
using Dmr;

var receiver = new DmrReceiver(
    new ReceiverOptions(SampleRate: 96000, ChannelOffsetHz: 12500),
    ev => Console.WriteLine(ev.ToJson()));

// Repeat for each incoming buffer. No minimum chunk size is required.
receiver.Push(iqBuffer.AsSpan()); // Complex[] iqBuffer supplied by your source

// At a known dropped-sample interval:
receiver.Discontinuity(missingSamples: 4096);

// Once, when the input ends:
receiver.Complete();
```

`Push` and callbacks are synchronous and single-consumer. They retain no caller buffer. Use one receiver per channel; drive it from one consumer task. For a live SDR, put the device adapter and bounded queue outside the decoder. If the queue drops samples, call `Discontinuity` with the gap length instead of joining unrelated sample ranges. The library has no file dependency and does not buffer the complete capture.

The channel resampler, mixer, pulse filter, acquisition state, slot state, and fragment assemblers retain state across calls. Frequency/timing estimation uses sync-assisted acquisition and constrained per-burst decision-directed timing. Receiver memory is bounded independently of capture duration. A blocking callback delays reception, so applications should keep callbacks short or enqueue events.

A receiver can be reused, for example from a pool of demodulators that a scanner binds to whichever channel is active:

```csharp
// Ends any open calls with reason "eof", then behaves exactly like a new receiver with these options.
receiver.Reset(new ReceiverResetOptions(CaptureId: "session-7-ch412", ChannelOffsetHz: 0,
    CentreFrequencyHz: 433_512_500, CaptureTime: DateTimeOffset.UtcNow));
```

`Reset` and `Discontinuity` reuse the receiver's storage; they allocate only the event they emit (`capture_info` or `sync_lost`). The sample rate, polarity, conjugation, symbol output and privacy keys stay those the receiver was constructed with. The resampler's polyphase table depends only on the sample rate and is shared by all receivers at that rate, so a receiver's own state is about 1 MiB (mostly the framer's history), and construction takes well under a millisecond after the first receiver at a rate.

At EOF, the library closes sessions with reason `eof`. It does not synthesize future samples to complete a burst that still needs filter context. A call may end with `terminator`, `new_header`, `identity_changed`, `data_transition`, `sync_lost`, or `discontinuity`.

For already aligned bursts, use `Dmr.Protocol.ProtocolDecoder.Process`. Inputs are 264 unpacked bits, first transmitted bit first. The caller supplies the track, optional absolute slot, original sample position, and burst kind (`bs_voice`, `bs_data`, `ms_voice`, `ms_data`, `direct1_voice`, etc., or `embedded`). This path is independently testable without DSP.

The solution has two libraries. [src/Dmr.Protocol](src/Dmr.Protocol/Dmr.Protocol.csproj) holds everything above the symbol level: FEC, burst layouts, `ProtocolDecoder`, privacy (`DmraPrivacy`, keyrings), the AMBE channel adapter and WAV export. It has no DSP and no IQ input, so another front end that already has aligned bursts can reference it alone. [src/Dmr](src/Dmr/Dmr.csproj) is the IQ receiver (`DmrReceiver`, front end, framer, input readers) and references `Dmr.Protocol`; namespaces are the same in both, so a project that references `Dmr` sees every type as before.

## Output contract

Output is JSON Lines, validated against [schemas/dmr-event.schema.json](schemas/dmr-event.schema.json). Diagnostics and timing summaries go to stderr, leaving stdout usable as an event stream.

The envelope contains schema version, event sequence, capture ID, original RF sample position/time, relative track, absolute slot if known, session ID, and event-specific `data`. `track` 0/1 separates the alternating burst streams; it is **not** a claim about absolute slot numbering. Carrier-level events use track -1. `sequence` is emission order; reassembled messages carry the time at which their final fragment was observed.

`call_update` carries a metadata revision and provenance such as `voice_lc_header` or `embedded_lc`. Standard FID 0 group/private LC yields source/destination IDs and service options; so does FID `0x10` (DMRA/Motorola) with FLCO 0 or 3, which uses the same layout (as DSD-FME interprets it; the event keeps `fid`). Invalid integrity checks cannot establish trusted call identity. Manufacturer-specific FIDs and protected/unrecognized LC remain raw. Short LC destination hashes remain hashes, not full addresses.

Standard metadata LCs (FID 0, protect flag clear, FLCO 4 to 7 talker alias, FLCO 8 GPS info) alternate with the voice LC in the embedded LC, and may appear in a full LC. They never end a call or change its identity or privacy status. A talker alias full LC in a voice LC header slot is not a new call.

### Metadata and data events

These `type` values were added within schema version 1. `session_id` is the voice call's ID for metadata carried in the call, and null for data outside a voice call; data never starts a call.

- `talker_alias` (TS 102 361-2 talker alias header and blocks 1 to 3): `source_id` (from the call's LC, or null), `alias` (decoded so far), `format` (`7bit`, `iso8`, `utf8`, `utf16be`), `declared_length`, `blocks_received` (array of 0 to 3), `complete`, `raw_hex` (the 4 x 7 information octets, missing blocks as zeros), `provenance`. Header and blocks are assembled per track across superframes. Text is decoded only from the header and the blocks that follow it without a gap, up to the declared length. An event is emitted whenever the decoded text grows, and once when it becomes `complete`. The state is cleared at call end and on an identity change. For `utf8` the declared length is taken as an octet count (as MMDVM-based implementations generate it), for `utf16be` as 16-bit code units, otherwise as characters.
- `location`: `latitude`, `longitude` (degrees), `position_error` (the 3-bit code; 7 = not known), `source` (`lc_gps` for the GPS info LC), `source_id`, `raw_hex`. Repeated identical GPS LCs within a call are reported once.
- `data_header`: in addition to the fields above, `dpf_name` and the format-specific fields. Unconfirmed and confirmed: `header_compression`, `pad_octet_count`, `full_message`, `blocks_to_follow`, `fsn` (confirmed also `resync`, `send_sequence`). Response: `response_class`, `response_type`, `response_status`. Defined short data: `appended_blocks`, `dd_format`, `dd_format_name`, `sarq`, `bit_padding`. Raw and status/precoded short data: `short_data_kind`, `source_port`, `destination_port`, `status_precoded`. UDT: `udt_format`, `pad_nibbles`, `udt_appended_blocks`, `supplementary_flag`, `protect_flag`, `udt_opcode`. Proprietary second header: `mfid`, `second_header`.
- `data_packet`, one per reassembled packet: `source_id`, `destination_id`, `group`, `sap`, `dpf`, `dpf_name`, `confirmed`, `rate` (`half`, `three_quarters`, `one`), `blocks`, `blocks_expected`, `complete`, `crc_valid` (message CRC-32, or CRC-16 for UDT), `bytes_hex` (user data without pad octets and CRC), `header_hex` (the first header's ten octets), `blocks_hex` (every decoded block octet in arrival order, including serial numbers, CRC-9, pad and message CRC, for analysing formats from stored events), `fec_failures`, `corrected_bits`. Where they apply: `crc9_failures`, `pad_octet_count`, `bit_padding`, `user_bits`, `dd_format`, `udt_format`, `pad_nibbles`, `second_header_hex`, `mfid`, `crc_note`, and for IPv4/UDP `ip_source`, `ip_destination`, `ip_checksum_valid`, `udp_source_port`, `udp_destination_port`, `udp_payload_hex`. `application` names the decoder that applied (`etsi_short_data`, `etsi_udt`, `motorola_tms`, `motorola_lrrp`, `hytera`, `udp`, `udp_compressed`) or is null. A packet cut short (by voice, an LC header or terminator, a new data header, a gap of more than one second, a discontinuity or EOF) is still emitted with `complete` and `crc_valid` false, so unknown or damaged formats can be analysed from stored events.
- `text_message`: `source_id`, `destination_id`, `group`, `text`, `encoding`, `protocol` (`etsi_short_data`, `etsi_udt`, `motorola_tms`, `hytera`, `ip_udp_text`), `port` (UDP destination port; null for short data), `crc_valid` (copied from the packet), `confidence` (`high` when a known layout matched, `low` when the text was found by scanning the payload), and `header_hex` (the vendor header octets before the text) where there is one.

The data path decodes rate 1/2 (BPTC), rate 3/4 (Viterbi decoding of the Annex B.2.4 trellis code) and rate 1 blocks. It checks confirmed blocks' serial number and CRC-9, ordering blocks by serial number when every CRC-9 holds. It reassembles packets per track from the header's block count, strips pad octets (pad bits for short data, pad nibbles for UDT) and checks the message CRC (B.3.9: 16-bit words with the least significant octet first; the CRC itself least significant octet first). A proprietary packet's second header (SAP 9, DPF 1111) is reported, not counted as user data. Applications:

- ETSI defined short data (DPF 1101): every text DD format of table 9.50 (7-bit, the ISO 8859 parts .NET provides, UTF-8, UTF-16 with optional BOM, UTF-16BE/LE, UTF-32); BCD as digits; binary only as `data_packet`.
- UDT (DPF 0000) text formats 7-bit, ISO 8-bit, 16-bit Unicode (taken as UTF-16BE) and BCD. The UDT appended-block CRC mask is not in the specification version in this repository, so the CRC is tried with a few candidate masks and the one that matched is reported in `crc_note`.
- IPv4/UDP over SAP 4 (full headers only; with compressed headers, SAP 2/3, the header is not parsed and only a clearly textual payload gives an `ip_udp_text` message): Motorola TMS on UDP 4007, Hytera text messaging on UDP 5016 and 3009 (5016 is also used by TYT/Baofeng MD-380 family radios), UTF-16LE text. When the vendor header does not match the expected layout, the text is taken from the longest plausible UTF-16LE run and marked `confidence` `low`. Other ports give `ip_udp_text` (`low`) only when most of the payload is Latin/Greek/Cyrillic UTF-16LE or ASCII text.
- Motorola LRRP (UDP 4001) is recognised (`application` `motorola_lrrp`) but not decoded to `location`: its token layout could not be verified, so the packet stays a `data_packet` for later analysis.

Each `vocoder_frame` event contains:

- `payload_hex`: exactly nine bytes, in received 72-bit air order, MSB first. These bits have not been corrected or decrypted.
- `frame_sequence`, `frame_index_in_burst`, `superframe_phase`, and 20 ms relative `playout_offset_ms` / `duration_ms`.
- `ambe49_hex`: optional seven-byte packed codec parameter payload, after AMBE mapping and channel correction; the final seven low bits are zero.
- `channel_decode_valid` and `corrected_bits`: AMBE protected-block decoding results. They are not end-to-end voice integrity or BER measurements.
- `privacy_status`, `ambe49_status`, and current metadata revision.
- `cipher49_hex`, `privacy_frame_index` (0..17), `privacy_message_indicator`: the corrected encrypted 49 bits and their superframe position and MI, with or without a key, for deferred decryption (see [PRIVACY.md](docs/PRIVACY.md)).

The 49-bit candidate is supplied after a recognized standard LC profile with no indicated privacy, or after supported known-key privacy decoding, and successful channel correction. Decrypted candidates carry `ambe49_status` and `decryption_status` equal to `decrypted_unverified`; a wrong key cannot be detected from FEC success. Privacy profile, algorithm ID, key ID and current message indicator accompany the frames, without key material. Some voice bits are unprotected; `voice_crc_available` is always false. Unsupported profile, privacy, and vendor frames remain available as raw 72-bit frames. No invented silence is inserted: lost bursts in an established voice stream produce `erasure` events preserving the playout sequence.

RF time identifies the burst's first symbol position. All three voice frames in the burst share that RF time; their reconstructed playout offsets differ by 20 ms. Neither quantity claims knowledge of the speaker's original absolute recording time.

## Implemented scope

| Area | Status |
|---|---|
| WAV/raw IQ, streaming API, bounded buffers, discontinuities | Implemented |
| Mixer, channel filter/resampling, FM discriminator, RRC, sync and timing | Implemented; no FFT required |
| Both repeater slots, MS streams, TDMA direct slot-specific sync | Implemented and tested |
| Golay SLOT, QR EMB, Hamming TACT, BPTC, RS, CRC checks/masks | Implemented |
| Voice LC header/terminator, embedded LC, CACH Short LC | Implemented |
| Conventional CSBK envelope/opcode and data-header fields | Implemented; unknown fields remain raw |
| 72-bit extraction and AMBE 49-bit channel adapter | Implemented |
| Known-key DMRA ARC4 voice decryption | PI-header acquisition, per-track cipher/MI state and loss advancement; reference vectors, synthetic IQ and a real AnyTone radio capture |
| Known-key DMRA AES-128 / AES-256 voice decryption | Same acquisition and state; NIST OFB and independent IV/keystream checks, synthetic IQ; no radio capture yet |
| Deferred decryption of stored frames (`DmraPrivacy`) | Ciphertext, frame index and MI emitted without a key; stateless keystream/decrypt API shared with the live path |
| WAV export for eligible clear or decrypted voice frames | Implemented with externally supplied mbelib 1.3.0 |
| Soft-symbol diagnostic export | Implemented; signalling FEC currently uses hard decisions |
| Talker alias (all four formats) and GPS info LC | Implemented; spec-built vectors only, not yet checked against a real radio |
| Data headers (all DPFs), rate 1/2, 3/4 (trellis) and 1 blocks, CRC-9, packet reassembly, CRC-32 | Implemented; spec-built vectors only, not yet checked against a real radio |
| Defined short data and UDT text, IPv4/UDP, Motorola TMS and Hytera text | Implemented; ETSI formats from TS 102 361-1, vendor layouts from public reverse engineering; spec-built vectors only |
| Compressed UDP/IP headers, LRRP and other location formats, multi-fragment reassembly, response packet semantics | Deferred; raw `data_packet` retained |
| Standalone reverse-channel interpretation, MBC semantic reassembly | Deferred; embedded single-fragment content retained in raw bursts |
| DES and other privacy profiles, encrypted late-entry MI recovery, vendor LC adapters, SDR device bindings, channel scanning, Tier III | Deferred |

See [DMR_DEMODULATOR_PLAN.md](DMR_DEMODULATOR_PLAN.md) for the roadmap and [docs/VALIDATION.md](docs/VALIDATION.md) for evidence and remaining validation gates. The initial voice/metadata path is implemented; the entire roadmap is not claimed complete.

## Tests and reference regeneration

The dependency-free console test harness returns a nonzero exit code on failure. It tests ETSI Annex D vectors, codeword correction, an independently compiled upstream C AMBE reference, metadata isolation, both slots, late entry, inversion, sample-rate conversion, gaps, noise, and the provided recording. The metadata and data tests build their vectors from the ETSI layouts: talker alias in every format, split across superframes and with a lost block; the GPS LC; packets at every rate, confirmed and unconfirmed, with good and bad CRCs; short data text; Motorola TMS and Hytera text over IPv4/UDP. They check the rate 3/4 trellis decoder on every state transition and on injected errors. No real-radio capture with talker alias, GPS or data has been decoded yet. The on-air capture `ota_433500_250k` (clear voice only) decodes byte-identically before and after the data path was added.

```powershell
dotnet run --project tests/Dmr.Tests -c Release
dotnet run --project tests/Dmr.Tests -c Release -- --benchmark output/characterization.json
```

### Processing throughput

For nonempty input, the CLI prints the input rate, processing rate, real-time multiple, and processing milliseconds per second of input to stderr:

```powershell
dotnet run --project src/Dmr.Cli -c Release -- dmr_test.wav --offset -12500 --output output/rate-events.jsonl
dotnet run --project tests/Dmr.Tests -c Release -- --throughput output/throughput.json
```

For `N` IQ sample pairs at input rate `Fs` and measured wall time `T` seconds:

- Input duration = `N / Fs` seconds.
- Processing rate = `N / T` IQ samples/second.
- Real-time multiple = `(N / Fs) / T`. Above `1x` keeps up with input on average; `10x` means one second of input takes 100 ms to process.
- Processing milliseconds per input second = `1000 * T / (N / Fs)`. Below 1000 ms keeps up on average. This is wall time, not CPU utilization.

The CLI timing includes receiver initialization, file reading, decoding, JSONL writing/flushing, and WAV completion when enabled; it excludes application startup and opening the input/output files. Output callbacks therefore affect this rate.

`--throughput` runs independently of the correctness suite. It replays the supplied recording and synthetic two-slot traffic at 48, 96, 192, and 384 ksample/s, with two warm-ups and five measured runs per case. Each replay uses a fresh receiver and 8192-sample chunks, and checks the processed sample and recovered voice-frame counts. Console output and the JSON report include median timing, min/max timing, and rates; JSON also retains every measured run and runtime details. These measurements include receiver initialization and event counting, but exclude input loading/generation, JSON serialization, file output, and speech synthesis. Run on an otherwise idle machine in Release mode. Average throughput does not establish worst-case chunk latency or guarantee drop-free live reception.

Measured acquisition optimization results and validation are recorded in [docs/FRAMER_PERFORMANCE.md](docs/FRAMER_PERFORMANCE.md).
Resampling optimization results, including input-rate comparisons, are recorded in [docs/FRONTEND_PERFORMANCE.md](docs/FRONTEND_PERFORMANCE.md).

### Reference regeneration

Development-only regeneration tools require Python and `pymupdf`; AMBE reference regeneration also needs GCC and network access. These tools are not part of the receiver's runtime or normal test execution:

```powershell
python tools/generate_spec_tables.py
python tools/make_reference_vectors.py
python tools/make_arc4_vectors.py
```

Reference revisions and upstream license notices are recorded in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Generated reference fixtures are checked in under `tests/fixtures`; large generated event streams and published binaries live under ignored `output/`.

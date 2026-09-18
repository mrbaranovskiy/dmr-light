# DMR IQ receiver

A managed C#/.NET 8 receiver that converts recorded or incrementally supplied IQ into DMR signalling events and vocoder frames. It decodes one configured RF channel, including both TDMA slots. It does not synthesize audio or decrypt privacy-protected traffic.

## Build and run

Requires the .NET 8 SDK (8.0.300 or a later 8.0 feature band, selected by `global.json`). There are no runtime NuGet packages, native libraries, FFTW dependencies, or network calls.

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

`--offset` is the selected carrier's frequency relative to IQ centre, in Hz; positive means above centre. It is required when the signal is off-centre. Automatic channel discovery is not implemented. `--centre-frequency` optionally records the capture's absolute centre frequency. `--capture-time` records an ISO 8601 start time. DSP polarity is resolved automatically using validated signalling; `--polarity normal|inverted` constrains that decision. `--conjugate` conjugates IQ before frequency translation.

Input rates from 24 to 384 ksample/s are accepted. Channelize wider SDR streams before this receiver. WAV input must contain two channels, I then Q, with PCM16/24/32 or float32 samples. Raw input supports interleaved little-endian `cf32`, `cs16`, `cs32`, or unsigned `cu8`. Discriminator-audio WAV is not IQ.

`--chunk-size` changes input buffering, without changing output. `--symbols` includes per-burst symbol values, hard dibits, squared-distance costs for `00,01,10,11`, residual frequency error, and uncalibrated relative power. These costs are not calibrated log likelihoods.

## Supplied recording

`dmr_test.wav` contains 635,136 stereo PCM32 IQ samples at 54,688 Hz, approximately 11.614 seconds. At offset -12,500 Hz the current receiver extracts:

- 72 voice bursts / **216 raw 72-bit vocoder frames**.
- Five valid voice LC headers, twelve valid embedded LC messages, and a valid terminator.
- Colour code 15; mobile-station sync; absolute slot number unavailable.
- Feature ID `0x68`, FLCO `0`, and raw LC `0068400044C000418C`.

The user identifies this capture as encrypted. Its manufacturer-specific LC is preserved as opaque: the receiver does not assume that its source/destination field layout is the standardized layout. Source and destination IDs remain null, privacy status is `unknown_vendor`, and `ambe49_hex` is null. This intentionally preserves the channel frames for a downstream vendor/privacy adapter. The file is a regression fixture, not an independently labelled RF conformance recording.

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

At EOF, the library closes sessions with reason `eof`. It does not synthesize future samples to complete a burst that still needs filter context. A call may end with `terminator`, `new_header`, `identity_changed`, `data_transition`, `sync_lost`, or `discontinuity`.

For already aligned bursts, use `Dmr.Protocol.ProtocolDecoder.Process`. Inputs are 264 unpacked bits, first transmitted bit first. The caller supplies the track, optional absolute slot, original sample position, and burst kind (`bs_voice`, `bs_data`, `ms_voice`, `ms_data`, `direct1_voice`, etc., or `embedded`). This path is independently testable without DSP.

## Output contract

Output is JSON Lines, validated against [schemas/dmr-event.schema.json](schemas/dmr-event.schema.json). Diagnostics and timing summaries go to stderr, leaving stdout usable as an event stream.

The envelope contains schema version, event sequence, capture ID, original RF sample position/time, relative track, absolute slot if known, session ID, and event-specific `data`. `track` 0/1 separates the alternating burst streams; it is **not** a claim about absolute slot numbering. Carrier-level events use track -1. `sequence` is emission order; reassembled messages carry the time at which their final fragment was observed.

`call_update` carries a metadata revision and provenance such as `voice_lc_header` or `embedded_lc`. Standard FID 0 group/private LC yields source/destination IDs and service options. Invalid integrity checks cannot establish trusted call identity. Manufacturer-specific FIDs and protected/unrecognized LC remain raw. Short LC destination hashes remain hashes, not full addresses.

Each `vocoder_frame` event contains:

- `payload_hex`: exactly nine bytes, in received 72-bit air order, MSB first. These bits have not been corrected or decrypted.
- `frame_sequence`, `frame_index_in_burst`, `superframe_phase`, and 20 ms relative `playout_offset_ms` / `duration_ms`.
- `ambe49_hex`: optional seven-byte packed codec parameter payload, after AMBE mapping and channel correction; the final seven low bits are zero.
- `channel_decode_valid` and `corrected_bits`: AMBE protected-block decoding results. They are not end-to-end voice integrity or BER measurements.
- `privacy_status`, `ambe49_status`, and current metadata revision.

The 49-bit candidate is supplied only after a recognized standard LC profile with no indicated privacy and successful channel correction. Some voice bits are unprotected; `voice_crc_available` is always false. Unknown profile, privacy, and vendor frames remain available as raw 72-bit frames. No invented silence is inserted: lost bursts in an established voice stream produce `erasure` events preserving the playout sequence.

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
| 72-bit extraction and AMBE 49-bit channel adapter | Implemented; no speech synthesis/decryption |
| Soft-symbol diagnostic export | Implemented; signalling FEC currently uses hard decisions |
| Full rate 1/2, 3/4, 1 packet reassembly and Part 3 applications | Deferred M6; burst types/raw payloads retained |
| Standalone reverse-channel interpretation, MBC semantic reassembly | Deferred; embedded single-fragment content retained in raw bursts |
| Vendor LC/privacy adapters, SDR device bindings, channel scanning, Tier III | Deferred |

See [DMR_DEMODULATOR_PLAN.md](DMR_DEMODULATOR_PLAN.md) for the roadmap and [docs/VALIDATION.md](docs/VALIDATION.md) for evidence and remaining validation gates. The initial voice/metadata path is implemented; the entire roadmap is not claimed complete.

## Tests and reference regeneration

The dependency-free console test harness returns a nonzero exit code on failure. It tests ETSI Annex D vectors, codeword correction, an independently compiled upstream C AMBE reference, metadata isolation, both slots, late entry, inversion, sample-rate conversion, gaps, noise, and the provided recording.

```powershell
dotnet run --project tests/Dmr.Tests -c Release
dotnet run --project tests/Dmr.Tests -c Release -- --benchmark output/characterization.json
```

Development-only regeneration tools require Python and `pymupdf`; AMBE reference regeneration also needs GCC and network access. These tools are not part of the receiver's runtime or normal test execution:

```powershell
python tools/generate_spec_tables.py
python tools/make_reference_vectors.py
```

Reference revisions and upstream license notices are recorded in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Generated reference fixtures are checked in under `tests/fixtures`; large generated event streams and published binaries live under ignored `output/`.

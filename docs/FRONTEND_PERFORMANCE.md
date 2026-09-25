# FrontEnd resampling optimization

Measured on 2026-09-25 using an Intel Core i7-14700, Windows 11, .NET 8.0.30 and Release builds. The baseline is commit `f345eb8`, which already includes the Framer optimization. These results measure the additional FrontEnd improvement.

## Change

The resampling multiply/accumulate was the largest part of `FrontEnd.Push`. Each filter tap performed circular addressing, startup handling, a jagged coefficient lookup, and the nonfinite checks in .NET's complex-by-real multiplication.

The updated implementation:

- Writes each mixed sample into two copies of the circular buffer. A filter window is then one contiguous span, including when it crosses the wrap point.
- Skips absent startup samples once per window and selects the phase's coefficient span once.
- Records the absolute index of the latest nonfinite mixed sample. When that index precedes the current window, every included sample is finite, so real and imaginary components can accumulate directly in the original tap order.
- Uses the original `Complex` multiplication when a window might contain a nonfinite value. Finite input IQ can overflow during mixing, so this fallback is necessary to preserve the [nonfinite behavior of .NET 8's Complex operators](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Runtime.Numerics/src/System/Numerics/Complex.cs#L164-L181).

Both scalar accumulators start at positive zero and use the same operands, multiplication and addition order as before. Mixing, coefficient generation, output scheduling, discriminator threshold, and the downstream filters are unchanged. A nonfinite accumulator resulting from otherwise finite sample products retains the original arithmetic behavior.

The extra ring storage is 4 KiB for the supplied recording and at most 32 KiB at 384 ksample/s, plus one 64-bit index. Memory remains bounded independently of capture duration.

## Measurements

The supplied recording contains 635,136 IQ samples at 54,688 Hz: 11.614 seconds of input. Each replay creates a receiver at offset -12,500 Hz, pushes preloaded samples in 8192-sample chunks, completes reception, and counts events. File I/O, JSON serialization, and speech synthesis are excluded.

Timing ran without a profiler in baseline / optimized / optimized / baseline order. Each process performed two warm-ups and 30 measured replays, for **60 measured replays per version**.

| Metric per recording | Before | After |
|---|---:|---:|
| Mean decode time | 0.2096 s | 0.1680 s |
| Median decode time | 0.2100 s | 0.1668 s |
| Observed range | 0.2023–0.2239 s | 0.1639–0.1834 s |
| Aggregate speed versus real time | 55.42x | 69.14x |
| Mean managed bytes allocated | 4,171,176 | 4,175,280 |

This is **19.8% less elapsed time**, or **24.8% higher throughput**. The 4,104-byte allocation increase comes from the larger sample buffer and additional state. Allocated bytes are cumulative on the decoding thread, not retained memory or peak working set.

An initial 20-replay comparison measured 0.210 s before, 0.186 s with only the contiguous-buffer change, and 0.167 s with both improvements.

Separate throughput-suite runs used the same baseline / optimized / optimized / baseline order, with two warm-ups and five measured replays per case per process. The following means combine ten measured replays per version. Each synthetic fixture contains about 1.962 seconds of two-slot traffic.

| Synthetic input rate | Before mean | After mean | Before speed | After speed | Less elapsed time |
|---|---:|---:|---:|---:|---:|
| 48 ksample/s | 30.98 ms | 25.42 ms | 63.34x | 77.20x | 17.9% |
| 96 ksample/s | 45.46 ms | 32.20 ms | 43.16x | 60.94x | 29.2% |
| 192 ksample/s | 72.53 ms | 47.80 ms | 27.05x | 41.05x | 34.1% |
| 384 ksample/s | 124.24 ms | 76.26 ms | 15.79x | 25.73x | 38.6% |

Longer resampling filters at higher rates make the same loop improvement more valuable. These observations depend on the workload, runtime and machine load; they are not worst-case latency guarantees.

## VTune comparison

Separate user-mode CPU profiles covered two warm-ups and 100 measured recording replays per version. `FrontEnd.Push` self CPU time fell from **8.918 s to 4.885 s**, about **45.2%**. Total sampled CPU time fell from 21.762 s to 18.115 s. Sampling and profiler overhead mean these numbers differ from the unprofiled wall-time results above.

The final profile's largest functions are `Fir.Push` (39.1%), `FrontEnd.Push` (27.0%), and `Framer.SearchSync` (13.4%). FIR filtering is the next largest optimization opportunity; it was not modified in this change.

## Validation

- Release build succeeds with no warnings or errors; all **27 tests pass**.
- Added a regression test for exact voice payloads in both slots at the 24 and 384 ksample/s input-rate limits, using small chunks and enough input to cross the ring repeatedly.
- The **35 receiver differential cases** produce identical complete ordered event JSON, including timestamps and symbol diagnostics.
- **46 direct FrontEnd cases** compare **266,452 frequency/power output pairs** bit-for-bit. Coverage includes rates from 24 to 384 ksample/s, positive/negative/zero offsets, conjugation, startup boundaries, repeated wraps, silence, impulses, noise, tones, chirps, large/small finite inputs, signed zero and subnormals. Targeted maximum-finite input cases test mixer overflow and subsequent recovery.
- The differential harness records the loaded assembly identity, and copied DLL hashes were checked. Every recording benchmark replay verifies sample, frame, event and valid-PDU counts; throughput-suite replays verify sample and frame counts.

These fixtures establish equivalence for the tested inputs; they are not an exhaustive proof for all floating-point data.

## Reproduction and local artifacts

```powershell
dotnet build Dmr.sln -c Release
dotnet run --project tests/Dmr.Tests -c Release --no-build
dotnet run --project tests/Dmr.Tests -c Release --no-build -- --throughput output/throughput.json
```

Investigation artifacts are retained under ignored `output/frontend-investigation/`:

- [Recording comparison](../output/frontend-investigation/comparison.json), with raw batches `before-1.json`, `after-1.json`, `after-2.json`, and `before-2.json`.
- [Input-rate comparisons](../output/frontend-investigation/throughput/comparison.json) and [throughput harness instructions](../output/frontend-investigation/throughput/README.md).
- [Exact raw-output harness and commands](../output/frontend-investigation/equivalence/README.md), baseline/candidate results, and `events-variant2.json` for the receiver-level comparison.
- [Before VTune summary](../output/frontend-investigation/before-summary.txt), [after summary](../output/frontend-investigation/after-summary.txt), [after hotspots](../output/frontend-investigation/after-hotspots.csv), and [source attribution](../output/frontend-investigation/after-FrontEnd-source.txt). Full sessions are `vtune-before/` and `vtune-after/`.

The preserved local benchmark executables can be replayed with:

```powershell
./output/frontend-investigation/bench-before/Profile.exe dmr_test.wav memory 30 output/frontend-investigation/recheck-before.json
./output/frontend-investigation/bench-variant2/Profile.exe dmr_test.wav memory 30 output/frontend-investigation/recheck-after.json
```

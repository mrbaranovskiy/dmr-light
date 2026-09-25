# Framer acquisition optimization

Measured on 2026-09-25 using an Intel Core i7-14700, Windows 11, .NET 8.0.30 and a Release build. The supplied recording's decode time decreased by **42.5%**, with **96.4% fewer managed bytes allocated** on the decoding thread.

## Change

While searching for synchronization, `Framer.Push` previously called the general `Fit` routine for all four patterns on every eligible sample. Each call independently interpolated the same 24 positions, calculated the same window statistics, and allocated a candidate even when the score failed. The candidate lookup also captured local variables in a lambda.

`SearchSync` now:

- Reads one 24-sample stack buffer and shares its sum and variance across patterns.
- Uses direct ring-buffer reads at acquisition's fixed integer sample positions. The neighbor's zero-weight term preserves the existing interpolation expression's floating-point behavior.
- Rejects low-variance windows and out-of-range gains before further scoring work.
- Allocates a candidate only after it passes the existing score threshold, and searches existing candidates with an ordered loop without a closure.

The acquisition window spans `index-231` through `index-1`. Search starts only when `index>800`, so every sample and its interpolation neighbor is available and inside the ring's retention window. Both ring indices are masked, including wraparound.

The existing fractional `Fit` used during tracking is unchanged. Pattern order, ascending dot-product accumulation, score/gain thresholds, acquisition scan frequency, candidate replacement rules and pending-candidate validation are retained. A failed window search still allows existing candidates to be validated.

## Measurements

The workload is `dmr_test.wav`: 635,136 IQ samples at 54,688 Hz, 11.614 seconds of input, offset -12,500 Hz. Input is preloaded; each replay creates a new receiver, pushes 8192-sample chunks, completes reception, and counts events. File reading, JSON writing and speech synthesis are excluded.

Measurements ran in baseline / optimized / optimized / baseline order, with two warm-ups and 30 measured replays in each batch: 60 measured replays per version. Timing runs had no profiler attached. Allocations use `GC.GetAllocatedBytesForCurrentThread`; they are cumulative allocated bytes, not retained memory or peak working set.

| Metric per recording | Before | After |
|---|---:|---:|
| Mean decode wall time | 0.3703 s | 0.2131 s |
| Median decode wall time | 0.3684 s | 0.2119 s |
| Observed wall-time range | 0.3576–0.3981 s | 0.2077–0.2228 s |
| Aggregate speed versus real time | 31.36x | 54.51x |
| Mean allocated bytes, MiB | 109.62 | 3.98 |

This is 42.5% less elapsed time, or 73.8% higher throughput, for this workload. Long periods before synchronization make this recording particularly sensitive to acquisition costs; continuously synchronized traffic can benefit less.

A subsequent run of the shorter, five-replay throughput suite measured the recording at 0.278 s median (41.80x real time), and synthetic 48–384 ksample/s cases at 53.43x–12.68x real time. That separate run is retained in [throughput-final.json](../output/framer-investigation/throughput-final.json); absolute timings vary between runs. The improvement percentages above use the matching before/after benchmark batches, not comparisons between different harnesses or runs.

A separate VTune user-mode sample profile covered two warm-ups and 100 measured replays per version. Summed self CPU time across all `Framer` functions fell from 18.559 s to 3.764 s (about 80%). Grouping all Framer functions avoids attributing improvements merely to code moving from `Push` into `SearchSync`. The final profile's largest functions are `FrontEnd.Push` (40.4%), `Fir.Push` (30.7%), and `Framer.SearchSync` (12.3%). These sampled CPU percentages differ from wall time and vary with scheduling, JIT compilation and machine load.

## Validation

- Release build succeeds with no warnings or errors; all **26 tests pass**.
- Added a regression test for acquisition following three seconds of silence, crossing the ring buffer more than twice. It checks recovered payloads and the exact synchronization position.
- **35 differential cases** produce identical complete ordered event JSON before and after, including timestamps, scores, frequency estimates and soft-symbol diagnostics. Cases cover BS/MS/direct modes, 48–384 ksample/s, noise, tuning and clock error, polarity, chunk sizes, late entry, fades, gaps, adjacent carriers, and long noise/silence transitions.
- Every benchmark replay verifies all 635,136 samples, 216 voice frames, 395 events and 18 integrity-validated PDUs.

The differential fixtures establish equivalence for those inputs, rather than an exhaustive proof over every acquisition threshold edge.

## Reproduction and local artifacts

```powershell
dotnet build Dmr.sln -c Release
dotnet run --project tests/Dmr.Tests -c Release --no-build
dotnet run --project tests/Dmr.Tests -c Release --no-build -- --throughput output/throughput.json
```

The investigation's preserved binaries and raw reports are in ignored `output/framer-investigation/`:

- [Timing/allocation comparison](../output/framer-investigation/final-comparison.json), with batches `before-3.json`, `final-1.json`, `final-2.json`, and `before-4.json`.
- [Differential harness and commands](../output/framer-investigation/equivalence/README.md).
- [Final VTune summary](../output/framer-investigation/final-summary.txt), [hotspots](../output/framer-investigation/final-hotspots.csv), [source attribution](../output/framer-investigation/final-SearchSync-source.txt), and session directory `vtune-final/`.
- [Original VTune report](../output/vtune/REPORT.md) and `output/vtune/recording-memory/` session.

The preserved local benchmark binaries can be replayed with:

```powershell
./output/framer-investigation/bench-before/Profile.exe dmr_test.wav memory 30 output/framer-investigation/recheck-before.json
./output/framer-investigation/bench-final/Profile.exe dmr_test.wav memory 30 output/framer-investigation/recheck-after.json
```

Further Framer experiments could share dot-product work across patterns or avoid repeatedly validating a pending candidate before new burst data arrives. Those involve additional arithmetic/state changes and should be evaluated separately against the same differential fixtures. This change leaves those paths intact.

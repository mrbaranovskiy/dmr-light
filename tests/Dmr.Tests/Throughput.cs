using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dmr;
using Dmr.Input;

internal static class Throughput
{
    private const int WarmupRuns=2, MeasuredRuns=5, ChunkSize=8192;
    private sealed record Capture(string Name,Complex[] Iq,int SampleRate,double Offset,int ExpectedFrames);
    private sealed record Measurement(double decode_seconds,long samples_processed,int events,int vocoder_frames);

    public static void Run(string root,string output)
    {
        string input=Path.Combine(root,"dmr_test.wav");
        if(Path.GetFullPath(output).Equals(Path.GetFullPath(input),StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Report output must differ from the input recording.");
#if DEBUG
        const string configuration="Debug";
        Console.Error.WriteLine("Use -c Release for representative throughput results.");
#else
        const string configuration="Release";
#endif
        Console.WriteLine($"In-memory receiver throughput: {WarmupRuns} warm-ups, {MeasuredRuns} measured runs, {ChunkSize}-sample chunks.");
        Console.WriteLine("Timing includes receiver construction, Push, Complete and event counting; excludes IQ loading, JSON and audio output.");
        var measurements=new List<object>();
        foreach(var capture in Captures(input))
        {
            for(int i=0;i<WarmupRuns;i++) Replay(capture);
            var runs=Enumerable.Range(0,MeasuredRuns).Select(_=>Replay(capture)).ToArray();
            var seconds=runs.Select(x=>x.decode_seconds).Order().ToArray();
            double median=seconds[MeasuredRuns/2], duration=(double)capture.Iq.Length/capture.SampleRate;
            measurements.Add(new
            {
                name=capture.Name, input_samples=capture.Iq.Length, input_samples_per_second=capture.SampleRate,
                channel_offset_hz=capture.Offset, capture_seconds=duration, expected_vocoder_frames=capture.ExpectedFrames,
                median_decode_seconds=median, min_decode_seconds=seconds[0], max_decode_seconds=seconds[^1],
                processing_samples_per_second=capture.Iq.Length/median,
                realtime_multiple=duration/median,
                processing_ms_per_input_second=1000*median/duration,
                runs
            });
            Console.WriteLine($"{capture.Name}: input {capture.SampleRate:N0} IQ samples/s, {duration:F3}s; median {median:F3}s (range {seconds[0]:F3}-{seconds[^1]:F3}s); {capture.Iq.Length/median:N0} IQ samples/s; {duration/median:F2}x real time; {1000*median/duration:F2} ms/input second");
        }
        var report=new
        {
            measured_at_utc=DateTimeOffset.UtcNow, runtime=RuntimeInformation.FrameworkDescription,
            os=RuntimeInformation.OSDescription, architecture=RuntimeInformation.ProcessArchitecture.ToString(),
            logical_processor_count=Environment.ProcessorCount, configuration,
            warmup_runs=WarmupRuns, measured_runs=MeasuredRuns, chunk_size=ChunkSize,
            timing_scope="Wall time for a fresh receiver, Push, Complete and synchronous event counting. IQ is loaded/generated before timing; no JSON serialization, file output or speech synthesis. Warm-ups are excluded.",
            interpretation="Rates use median run time. realtime_multiple = capture_seconds / median_decode_seconds; >1 means faster than real time on average. processing_ms_per_input_second = 1000 / realtime_multiple. This is not CPU utilization or a worst-case latency guarantee.",
            measurements
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions { WriteIndented=true })+Environment.NewLine);
        Console.WriteLine($"Wrote throughput report: {output}");
    }

    private static IEnumerable<Capture> Captures(string input)
    {
        Complex[] iq; int rate;
        using(var reader=new IqReader(input))
        {
            rate=reader.SampleRate; iq=new Complex[checked((int)reader.Samples)];
            int position=0,n;
            while((n=reader.Read(iq.AsSpan(position)))>0) position+=n;
            if(position!=iq.Length) throw new InvalidDataException("Incomplete IQ recording.");
        }
        yield return new("dmr_test.wav",iq,rate,-12500,216);
        foreach(int sampleRate in new[]{48000,96000,192000,384000})
        {
            var fixture=Synthetic.Create(sampleRate,carrier:12500);
            yield return new($"synthetic_{sampleRate}",fixture.Iq,sampleRate,12500,
                fixture.Slot1Frames.Count+fixture.Slot2Frames.Count);
        }
    }

    private static Measurement Replay(Capture capture)
    {
        int events=0,frames=0;
        var watch=Stopwatch.StartNew();
        var receiver=new DmrReceiver(new(capture.SampleRate,capture.Offset),e=>
        {
            events++;
            if(e.Type=="vocoder_frame") frames++;
        });
        for(int position=0;position<capture.Iq.Length;position+=ChunkSize)
            receiver.Push(capture.Iq.AsSpan(position,Math.Min(ChunkSize,capture.Iq.Length-position)));
        receiver.Complete(); watch.Stop();
        // Reject a fast run that silently skipped input or failed to recover the expected traffic.
        if(receiver.SamplesProcessed!=capture.Iq.Length || frames!=capture.ExpectedFrames)
            throw new InvalidOperationException($"{capture.Name}: processed {receiver.SamplesProcessed}/{capture.Iq.Length} samples, recovered {frames}/{capture.ExpectedFrames} voice frames.");
        return new(watch.Elapsed.TotalSeconds,receiver.SamplesProcessed,events,frames);
    }
}

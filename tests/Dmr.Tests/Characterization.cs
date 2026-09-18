using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dmr;
using Dmr.Vocoder;

internal static class Characterization
{
    public static void Run(string output)
    {
        var measurements=new List<object>();
        foreach(double sigma in new[]{0.0,.025,.05,.1,.15,.2,.3})
        {
            int expected=0,recovered=0,exact=0,corrected=0; double elapsed=0,duration=0;
            for(int seed=1;seed<=3;seed++)
            {
                var fixture=Synthetic.Create(noise:sigma,seed:seed);
                var watch=Stopwatch.StartNew(); var events=Synthetic.Decode(fixture.Iq); elapsed+=watch.Elapsed.TotalSeconds;
                duration+=fixture.Iq.Length/48000.0;
                var air=new[]{fixture.Slot1Frames.ToHashSet(),fixture.Slot2Frames.ToHashSet()};
                var parameters=air.Select(frames=>frames.Select(x=>Bits.Hex(Ambe.Decode(Bits.Unpack(Convert.FromHexString(x))).Payload)).ToHashSet()).ToArray();
                expected+=fixture.Slot1Frames.Count+fixture.Slot2Frames.Count;
                foreach(var e in events.Where(e=>e.Type=="vocoder_frame"))
                {
                    recovered++;
                    if(e.Slot is not (1 or 2)) continue;
                    if(air[e.Slot.Value-1].Contains((string)e.Data["payload_hex"]!)) exact++;
                    if(e.Data["ambe49_hex"] is string bits && parameters[e.Slot.Value-1].Contains(bits)) corrected++;
                }
            }
            measurements.Add(new { noise_sigma_per_iq_component=sigma,
                input_snr_db=sigma==0?(double?)null:10*Math.Log10(.25/(2*sigma*sigma)),
                expected_frames=expected, emitted_frames=recovered, exact_received_72bit_frames=exact,
                exact_recovered_49bit_frames=corrected, decode_seconds=elapsed, capture_seconds=duration,
                realtime_multiple=duration/elapsed });
        }
        var report=new { runtime=RuntimeInformation.FrameworkDescription,os=RuntimeInformation.OSDescription,
            architecture=RuntimeInformation.ProcessArchitecture.ToString(),
            seeds=new[]{1,2,3},sample_rate=48000,
            snr_definition="0.25 mean signal power divided by 2*sigma^2 AWGN power across the entire 48 kHz complex input bandwidth; not Eb/N0 or sensitivity.",
            limitations="Three deterministic synthetic captures per point; empirical observations, no statistical confidence or RF compliance claim. Includes acquisition/header losses. Payload matches are compared against each slot's unique generated frames.",
            measurements };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions { WriteIndented=true })+Environment.NewLine);
        Console.WriteLine($"Wrote characterization: {output}");
    }
}

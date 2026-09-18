using System.Numerics;
using System.Text.Json;
using Dmr;
using Dmr.Fec;
using Dmr.Input;
using Dmr.Protocol;
using Dmr.Vocoder;

int passed=0,failed=0;
void Check(bool condition,string message="Assertion failed") { if(!condition) throw new Exception(message); }
void Test(string name,Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch(Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e.Message}\n{e.StackTrace}"); }
}
string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
Test("All ETSI codewords and all single-bit errors",()=>
{
    foreach(var code in new[]{Codes.Golay20,Codes.Qr16,Codes.Hamming7,Codes.Hamming13,Codes.Hamming15,Codes.Hamming16,Codes.Hamming17})
        for(uint d=0;d<(1u<<code.DataBits);d++)
        {
            Check(code.Decode(code.Encode(d)).Data==d);
            for(int i=0;i<code.Length;i++)
            { var result=code.Decode(code.Encode(d)^(1u<<i)); Check(result.Valid && result.Data==d && result.Corrections==1,$"({code.Length},{code.DataBits}) data={d}"); }
        }
});
Test("Golay triples, QR doubles, extended Hamming rejection",()=>
{
    for(int i=0;i<20;i++) for(int j=i+1;j<20;j++) for(int k=j+1;k<20;k++)
    { var d=Codes.Golay20.Decode(Codes.Golay20.Encode(0xad)^(1u<<i)^(1u<<j)^(1u<<k)); Check(d.Valid && d.Data==0xad); }
    for(uint data=0;data<128;data++) for(int i=0;i<16;i++) for(int j=i+1;j<16;j++)
    { var d=Codes.Qr16.Decode(Codes.Qr16.Encode(data)^(1u<<i)^(1u<<j)); Check(d.Valid && d.Data==data); }
    Check(!Codes.Hamming16.Decode(Codes.Hamming16.Encode(31)^3).Valid);
});
Test("ETSI Annex D idle vector and product correction",()=>
{
    var data=Bits.Unpack(Convert.FromHexString("FF83DF1732094ED1E7CD8A91"));
    // Figure D.1, thirteen 15-bit rows, transcribed independently of the encoder.
    string[] rows=["000111111110100","100000111101101","111110001011101","110011001000101","000100101000111","111011010001101","111100111110010","001101100011101","010100100010101","010011100100011","101100111101100","001000010000100","010010101110110"];
    byte[] matrix=[0,..string.Concat(rows).Select(x=>(byte)(x-'0'))];
    var air=ProductCodes.Encode196(data);
    for(int i=0;i<196;i++) Check(air[i*181%196]==matrix[i],$"Annex D bit {i}");
    foreach(int bit in Enumerable.Range(1,195))
    { var damaged=(byte[])air.Clone(); damaged[bit]^=1; var d=ProductCodes.Decode196(damaged); Check(d.Valid && d.Data.SequenceEqual(data)); }
});
Test("RS matrix, one-byte repair, CRC known answers",()=>
{
    Check(Convert.ToHexString(Checks.RsParity([1,0,0,0,0,0,0,0,0]))=="1CBCFD");
    var lc=Synthetic.Lc(); byte[] clean=[..lc,..Checks.RsParity(lc)];
    for(int pos=0;pos<12;pos++)
    { var w=(byte[])clean.Clone(); w[pos]^=0xa7; Check(Checks.CorrectRs(w,out int n) && n==1 && w.SequenceEqual(clean)); }
    var two=(byte[])clean.Clone(); two[1]^=3; two[5]^=7; Check(!Checks.CorrectRs(two,out _));
    var message=Bits.Unpack(System.Text.Encoding.ASCII.GetBytes("123456789"));
    Check(Checks.Crc(message,16,0x1021)==0x31c3); Check(Checks.Crc(message,8,7)==0xf4);
});
Test("Full LC masks and invalid integrity rejection",()=>
{
    var lc=Synthetic.Lc();
    foreach(bool terminator in new[]{false,true})
    { var d=Burst.DecodeData(Burst.MakeLc(lc,7,terminator)); Check(d.IntegrityValid && d.ColourCode==7 && d.Payload[..72].SequenceEqual(Bits.Unpack(lc))); }
    byte[] payload=[..lc,..Checks.RsParity(lc).Select(x=>(byte)(x^0x99))];
    Check(!Burst.DecodeData(Burst.MakeData(Bits.Unpack(payload),7,1)).IntegrityValid,"Wrong LC mask accepted");
});
Test("Embedded LC and common Short LC checks",()=>
{
    var lc=Bits.Unpack(Synthetic.Lc()); var air=ProductCodes.EncodeEmbedded(lc);
    Check(ProductCodes.DecodeEmbedded(air).Data.SequenceEqual(lc));
    for(int i=0;i<128;i+=8)
    { var bad=(byte[])air.Clone(); bad[i]^=1; var d=ProductCodes.DecodeEmbedded(bad); Check(d.Valid && d.Data.SequenceEqual(lc)); }
    var sl=Bits.FromUInt(0x1881234,28); var code=ProductCodes.EncodeShortLc(sl);
    Check(ProductCodes.DecodeShortLc(code).Valid && ProductCodes.DecodeShortLc(code).Data.SequenceEqual(sl));
});
Test("AMBE channel adapter against pinned upstream C decoder",()=>
{
    using var vectors=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/fixtures/ambe_reference.json")));
    foreach(var v in vectors.RootElement.GetProperty("vectors").EnumerateArray())
    {
        var decoded=Ambe.Decode(Bits.Unpack(Convert.FromHexString(v.GetProperty("air72").GetString()!)));
        Check(Bits.Hex(decoded.Payload)==v.GetProperty("payload49").GetString(),"Reference AMBE bits differ");
    }
});
Test("Middle voice frame crosses signalling field",()=>
{
    var socket=Enumerable.Range(0,216).Select(i=>(byte)(i%3==0?1:0)).ToArray();
    var burst=Synthetic.Voice(Synthetic.Lc(),0,7,socket);
    Check(Burst.VoiceSocket(burst).SequenceEqual(socket));
});
Test("Metadata, privacy, and session state isolation",()=>
{
    var events=new List<DmrEvent>(); var p=new ProtocolDecoder(48000,events.Add);
    p.Process(Burst.MakeLc(Synthetic.Lc(),7),100,0,1,"bs_data");
    p.Process(Burst.MakeLc(Synthetic.Lc(654321,92),7),1540,1,2,"bs_data");
    var socket=Enumerable.Repeat(Ambe.Encode(new byte[49]),3).SelectMany(x=>x).ToArray();
    p.Process(Synthetic.Voice(Synthetic.Lc(),0,7,socket),2980,0,1,"bs_voice");
    p.Process(Synthetic.Voice(Synthetic.Lc(654321,92),0,7,socket),4420,1,2,"bs_voice");
    var frames=events.Where(e=>e.Type=="vocoder_frame").ToArray();
    Check(frames.Length==6 && (int)frames[0].Data["source_id"]! ==123456 && (int)frames[3].Data["source_id"]! ==654321);
    p.Process(Burst.MakeLc(Synthetic.Lc(),7,true),5860,0,1,"bs_data");
    p.Process(Synthetic.Voice(Synthetic.Lc(),0,7,socket),8740,0,1,"bs_voice");
    Check(events.Last(e=>e.Type=="vocoder_frame").Data["source_id"]==null,"Stale caller leaked");
    p.Reset(10000); p.Process(Burst.MakeLc(Synthetic.Lc(service:64,fid:104),15),11000,0,null,"ms_data");
    p.Process(Synthetic.Voice(Synthetic.Lc(),0,15,socket,"ms"),13880,0,null,"ms_voice");
    var opaque=events.Last(e=>e.Type=="vocoder_frame");
    Check(opaque.Slot==null && opaque.Data["ambe49_hex"]==null && (string)opaque.Data["privacy_status"]! =="unknown_vendor");
});
Synthetic.Fixture? clean=null; List<DmrEvent>? baseline=null;
Test("End-to-end both slots, staggered superframes, exact frames and IDs",()=>
{
    clean=Synthetic.Create(); baseline=Synthetic.Decode(clean.Iq);
    for(int slot=1;slot<=2;slot++)
    {
        var frames=baseline.Where(e=>e.Type=="vocoder_frame" && e.Slot==slot).ToArray();
        var expected=slot==1?clean.Slot1Frames:clean.Slot2Frames;
        Check(frames.Length==expected.Count,$"slot {slot}: {frames.Length}/{expected.Count} frames; events {string.Join(',',baseline.GroupBy(e=>e.Type).Select(x=>$"{x.Key}={x.Count()}"))}");
        Check(frames.Select(e=>(string)e.Data["payload_hex"]!).SequenceEqual(expected),$"slot {slot} payload mismatch");
        Check(frames.All(e=>(int)e.Data["source_id"]! ==(slot==1?123456:654321)),"Incorrect source ID");
        Check(frames.Select(e=>(long)e.Data["playout_offset_ms"]!).SequenceEqual(Enumerable.Range(0,expected.Count).Select(i=>(long)i*20)));
    }
    Check(baseline.Any(e=>e.Type=="pdu" && Equals(e.Data.GetValueOrDefault("pdu_type"),"short_lc") && Equals(e.Data["integrity_valid"],true)));
});
Test("Chunk invariance, including single-sample calls",()=>
{
    Check(clean!=null && baseline!=null,"Baseline failed");
    var expected=baseline!.Select(e=>e.ToJson()).ToArray();
    foreach(int chunk in new[]{1,137,16384})
        Check(Synthetic.Decode(clean!.Iq,chunk:chunk).Select(e=>e.ToJson()).SequenceEqual(expected),$"Chunk {chunk} changed output");
});
Test("Inverted IQ resolves voice/data ambiguity",()=>
{
    Check(clean!=null && baseline!=null);
    var inverted=Synthetic.Decode(clean!.Iq.Select(Complex.Conjugate).ToArray());
    Check(inverted.Where(e=>e.Type=="vocoder_frame").Select(e=>e.Data["payload_hex"]).SequenceEqual(baseline!.Where(e=>e.Type=="vocoder_frame").Select(e=>e.Data["payload_hex"])));
});
Test("Arbitrary sample rate, residual tuning error and clock drift",()=>
{
    var fixture=Synthetic.Create(54688,carrier:13500,clockPpm:80,noise:.01);
    var events=Synthetic.Decode(fixture.Iq,54688,12500);
    Check(events.Count(e=>e.Type=="vocoder_frame")==144,$"Received {events.Count(e=>e.Type=="vocoder_frame")} frames");
    Check(events.Where(e=>e.Type=="vocoder_frame" && e.Slot==1).Select(e=>(string)e.Data["payload_hex"]!).SequenceEqual(fixture.Slot1Frames));
});
Test("Late entry without headers recovers embedded identity",()=>
{
    Check(clean!=null);
    var events=Synthetic.Decode(clean!.Iq[16000..]);
    Check(events.Any(e=>e.Type=="call_update" && Equals(e.Data.GetValueOrDefault("provenance"),"embedded_lc")),"No late-entry LC");
    Check(events.Count(e=>e.Type=="vocoder_frame")>36);
});
Test("TDMA direct mode assigns each slot from its own sync",()=>
{
    var fixture=Synthetic.Create(mode:"direct"); var events=Synthetic.Decode(fixture.Iq);
    foreach(int slot in new[]{1,2})
    {
        var frames=events.Where(e=>e.Type=="vocoder_frame" && e.Slot==slot).ToArray();
        Check(frames.Length==72,$"Direct slot {slot}: {frames.Length} frames");
        Check(frames.Select(e=>(string)e.Data["payload_hex"]!).SequenceEqual(slot==1?fixture.Slot1Frames:fixture.Slot2Frames));
    }
});
Test("MS sourced traffic keeps absolute slot unknown",()=>
{
    var fixture=Synthetic.Create(mode:"ms"); var events=Synthetic.Decode(fixture.Iq);
    Check(events.Count(e=>e.Type=="vocoder_frame")==144);
    Check(events.Where(e=>e.Type=="vocoder_frame").All(e=>e.Slot==null));
});
Test("Missing burst emits erasures and preserves later frame order",()=>
{
    Check(clean!=null); var iq=(Complex[])clean!.Iq.Clone();
    // Zero a complete same-slot burst in the middle of active voice, including filter tails.
    int begin=2880+10*1440;
    Array.Clear(iq,begin,1440);
    var events=Synthetic.Decode(iq);
    Check(events.Count(e=>e.Type=="erasure")>=3,"No erasures for the missing burst");
    Check(events.Count(e=>e.Type=="vocoder_frame")<144,"Missing voice was fabricated");
    Check(events.Any(e=>e.Type=="call_end" && Equals(e.Data["reason"],"terminator")),"Did not recover after fade");
});
Test("192 kHz capture resamples to exact frames",()=>
{
    var fixture=Synthetic.Create(192000,carrier:40000);
    var events=Synthetic.Decode(fixture.Iq,192000,40000);
    Check(events.Count(e=>e.Type=="vocoder_frame")==144);
    Check(events.Where(e=>e.Type=="vocoder_frame" && e.Slot==1).Select(e=>(string)e.Data["payload_hex"]!).SequenceEqual(fixture.Slot1Frames));
});
Test("Noise-only input produces no confirmed calls",()=>
{
    var rng=new Random(17); var noise=Enumerable.Range(0,48000*2).Select(_=>new Complex(rng.NextDouble()-.5,rng.NextDouble()-.5)).ToArray();
    Check(!Synthetic.Decode(noise).Any(e=>e.Type is "call_start" or "vocoder_frame" or "sync_acquired"));
});
Test("Adjacent DMR carrier is rejected by the channel filter",()=>
{
    Check(clean!=null);
    var other=Synthetic.Create(carrier:12500,seed:91);
    var iq=clean!.Iq.Zip(other.Iq,(a,b)=>a+b*2).ToArray();
    var frames=Synthetic.Decode(iq).Where(e=>e.Type=="vocoder_frame").ToArray();
    Check(frames.Length==144,$"Adjacent carrier test recovered {frames.Length}/144 frames");
    Check(frames.Where(e=>e.Slot==1).Select(e=>(string)e.Data["payload_hex"]!).SequenceEqual(clean.Slot1Frames));
});
Test("Raw sample parsing and malformed input rejection",()=>
{
    string dir=Path.Combine(Path.GetTempPath(),"dmr-tests-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
    string file=Path.Combine(dir,"samples.iq");
    try
    {
        File.WriteAllBytes(file,[0,0x40,0,0xc0]);
        using(var reader=new IqReader(file,"cs16",48000))
        { var sample=new Complex[1]; Check(reader.Read(sample)==1 && sample[0]==new Complex(.5,-.5)); Check(reader.Read(sample)==0); }
        File.WriteAllBytes(file,[0,1,2]); bool failedRead=false;
        try { using var reader=new IqReader(file,"cs16",48000); } catch(InvalidDataException) { failedRead=true; } Check(failedRead);
        bool badRate=false; try { _=new DmrReceiver(new(1000),_=>{}); } catch(ArgumentOutOfRangeException) {badRate=true;} Check(badRate);
        bool badOffset=false; try { _=new DmrReceiver(new(48000,20000),_=>{}); } catch(ArgumentOutOfRangeException) {badOffset=true;} Check(badOffset);
    }
    finally { File.Delete(file); Directory.Delete(dir); }
});
Test("Soft symbol diagnostics use dibit-order costs and original sample positions",()=>
{
    Check(clean!=null); var events=new List<DmrEvent>(); var r=new DmrReceiver(new(48000,EmitSymbols:true),events.Add);
    r.Push(clean!.Iq); r.Complete();
    var observation=(List<Dmr.Dsp.SoftSymbol>)events.First(e=>e.Type=="symbols").Data["observations"]!;
    Check(observation.Count==132);
    foreach(var s in observation) Check(s.Costs.Length==4 && Array.IndexOf(s.Costs,s.Costs.Min())==s.Dibit);
    Check(observation.Zip(observation.Skip(1),(a,b)=>b.RfSampleIndex-a.RfSampleIndex).All(x=>x is >=9 and <=11));
});
Test("Discontinuity and EOF close sessions with explicit reasons",()=>
{
    Check(clean!=null); var events=new List<DmrEvent>(); var r=new DmrReceiver(new(48000),events.Add);
    r.Push(clean!.Iq.AsSpan(0,25000)); r.Discontinuity(1000); r.Push(clean.Iq.AsSpan(26000)); r.Complete(); r.Complete();
    Check(events.Any(e=>e.Type=="call_end" && Equals(e.Data["reason"],"discontinuity")));
    Check(r.SamplesProcessed==clean.Iq.Length);
    bool threw=false; try {r.Push([Complex.Zero]);} catch(InvalidOperationException) {threw=true;} Check(threw);
});
Test("Supplied WAV format and observed protocol regression",()=>
{
    string path=Path.Combine(root,"dmr_test.wav");
    if(!File.Exists(path)) throw new Exception("Missing provided IQ fixture");
    using(var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/fixtures/capture_manifest.json"))))
    using(var input=File.OpenRead(path))
        Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).Equals(manifest.RootElement.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase),"Recorded fixture hash differs from manifest");
    using var reader=new IqReader(path); Check(reader.SampleRate==54688 && reader.Samples==635136 && reader.Format=="cs32");
    var events=new List<DmrEvent>(); var r=new DmrReceiver(new(reader.SampleRate,-12500),events.Add);
    var samples=new Complex[733]; int n; while((n=reader.Read(samples))>0) r.Push(samples.AsSpan(0,n)); r.Complete();
    var frames=events.Where(e=>e.Type=="vocoder_frame").ToArray();
    Check(frames.Length==216,$"Observed capture: {frames.Length} frames");
    Check(frames.All(e=>e.Slot==null && e.Data["ambe49_hex"]==null),"Opaque MS traffic mislabelled as clear/absolute slot");
    Check(events.Count(e=>e.Type=="pdu" && Equals(e.Data.GetValueOrDefault("integrity_valid"),true))==18);
    Check(events.Any(e=>e.Type=="call_update" && Equals(e.Data.GetValueOrDefault("fid"),104)));
});
Console.WriteLine($"\n{passed} passed, {failed} failed");
if(failed==0 && args.Length==2 && args[0]=="--benchmark") Characterization.Run(args[1]);
return failed==0?0:1;

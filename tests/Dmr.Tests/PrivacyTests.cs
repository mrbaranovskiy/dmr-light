using System.Buffers.Binary;
using System.Text.Json;
using Dmr;
using Dmr.Fec;
using Dmr.Privacy;
using Dmr.Protocol;
using Dmr.Vocoder;

internal static class PrivacyTests
{
    private sealed record Frame(string MessageIndicator,string Ciphertext49,string Plaintext49);
    private sealed record Case(byte KeyId,string KeyHex,string InitialMi,Frame[] Frames);
    private static void Check(bool ok,string message="Privacy assertion failed") { if(!ok) throw new Exception(message); }
    private static string? Field(DmrEvent e,string key)=>e.Data.GetValueOrDefault(key) as string;
    private static byte[] Unpack(string hex)=>Bits.Unpack(Convert.FromHexString(hex))[..49];
    private static Arc4Keyring Keys(params Case[] cases)=>new(cases.ToDictionary(x=>x.KeyId,x=>Convert.FromHexString(x.KeyHex)));
    private static byte[] Lc(int track=0)=>Synthetic.Lc(track==0?123456:654321,track==0?91:92,service:0x40);
    private static byte[] Pi(Case c,int algorithm=0x21,int fid=0x10,bool valid=true)
    {
        byte[] bytes=[(byte)algorithm,(byte)fid,c.KeyId,0,0,0,0,0,0,91,0,0];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(3,4),Convert.ToUInt32(c.InitialMi,16));
        uint crc=Checks.Crc(Bits.Unpack(bytes[..10]),16,0x1021,finalXor:0xffff)^0x6969u;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), (ushort)(crc^(valid?0u:1u)));
        return Burst.MakeData(Bits.Unpack(bytes),7,0);
    }
    private static byte[] Voice(Case c,int burst,int track=0)=>Synthetic.Voice(Lc(track),burst%6,7,
        c.Frames.Skip(burst*3).Take(3).SelectMany(f=>Ambe.Encode(Unpack(f.Ciphertext49))).ToArray());
    private static void Feed(ProtocolDecoder decoder,Case c,int burst,int track=0)=>
        decoder.Process(Voice(c,burst,track),3000+burst*2880+track*1440,track,track+1,burst%6==0?"bs_voice":"embedded");

    public static void Register(Action<string,Action> test,string root)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/fixtures/arc4_reference.json")));
        var cases=document.RootElement.GetProperty("cases").EnumerateArray().Select(c=>new Case(
            c.GetProperty("key_id").GetByte(),c.GetProperty("key_hex").GetString()!,c.GetProperty("initial_mi").GetString()!,
            c.GetProperty("frames").EnumerateArray().Select(f=>new Frame(f.GetProperty("message_indicator").GetString()!,
                f.GetProperty("ciphertext49").GetString()!,f.GetProperty("plaintext49").GetString()!)).ToArray())).ToArray();
        test("ARC4 RFC 6229 known answers at offsets 0, 256 and 4096",()=>
        {
            var expected=new Dictionary<int,string> { [0]="B2396305F03DC027CCC3524A0A1118A8",[256]="1CFCF62B03EDDB641D77DFCF7F8D8C93",[4096]="FF25B58995996707E51FBDF08B34D875" };
            foreach(var pair in expected)
            {
                var cipher=new Arc4([1,2,3,4,5]); cipher.Skip(pair.Key);
                Check(Convert.ToHexString(Enumerable.Range(0,16).Select(_=>cipher.Next()).ToArray())==pair.Value);
            }
        });
        test("ARC4 key file validation, ownership and no key disclosure",()=>
        {
            byte[] key=[1,2,3,4,5]; var ring=new Arc4Keyring(new Dictionary<byte,byte[]> { [7]=key }); key[0]=99;
            Check(ring.Find(7)!.SequenceEqual(new byte[] {1,2,3,4,5}));
            var json="{\"dmra_arc4\":[{\"key_id\":7,\"key_hex\":\"0102030405\"}]}";
            Check(Arc4Keyring.FromJson(json).Find(7)!.SequenceEqual(ring.Find(7)!));
            foreach(var bad in new[]{"{}",json.Replace("0102030405","invalid-key-secret"),json.Replace(":7",":256"),
                json.Replace("}]","},{\"key_id\":7,\"key_hex\":\"1122334455\"}]"),"{\"dmra_arc4\": [secret"})
            {
                try { Arc4Keyring.FromJson(bad); throw new Exception("Invalid key file accepted"); }
                catch(ArgumentException ex) { Check(!ex.ToString().Contains("secret") && !ex.ToString().Contains("1122334455")); }
            }
        });
        test("108 DMRA ARC4 frames against pinned C reference, including zero key",()=>
        {
            foreach(var c in cases)
            {
                var context=new DmraArc4Context(c.KeyId,Convert.ToUInt32(c.InitialMi,16),Keys(c));
                for(int n=0;n<c.Frames.Length;n++)
                {
                    if(n%3==0) context.AlignBurst(n/3%6,n%18==0);
                    Check(context.MessageIndicator.ToString("X8")==c.Frames[n].MessageIndicator);
                    Check(Bits.Hex(context.Transform(Unpack(c.Frames[n].Ciphertext49),true)!)==c.Frames[n].Plaintext49);
                }
            }
        });
        test("DMRA ARC4 PI parsing and simultaneous independent slots",()=>
        {
            var events=new List<DmrEvent>(); var decoder=new ProtocolDecoder(48000,events.Add,privacyKeys:Keys(cases));
            for(int track=0;track<2;track++)
            {
                decoder.Process(Burst.MakeLc(Lc(track),7),0,track,track+1,"bs_data");
                decoder.Process(Pi(cases[track],track==0?0x21:0x01),1000,track,track+1,"bs_data");
            }
            for(int burst=0;burst<12;burst++) for(int track=0;track<2;track++) Feed(decoder,cases[track],burst,track);
            for(int track=0;track<2;track++)
            {
                var frames=events.Where(e=>e.Type=="vocoder_frame" && e.Track==track).ToArray(); Check(frames.Length==36);
                for(int n=0;n<frames.Length;n++)
                {
                    Check(Field(frames[n],"ambe49_hex")==cases[track].Frames[n].Plaintext49);
                    Check(Field(frames[n],"privacy_message_indicator")==cases[track].Frames[n].MessageIndicator);
                    Check(Field(frames[n],"decryption_status")=="decrypted_unverified");
                    Check(Field(frames[n],"privacy_status")=="indicated");
                    Check(Field(frames[n],"payload_hex")==Bits.Hex(Ambe.Encode(Unpack(cases[track].Frames[n].Ciphertext49))));
                }
            }
            string output=string.Join("\n",events.Select(e=>e.ToJson()));
            Check(!output.Contains(cases[0].KeyHex) && !output.Contains(cases[1].KeyHex));
        });
        test("DMRA ARC4 alignment across lost B, F, A bursts and failed channel frames",()=>
        {
            var c=cases[0]; var events=new List<DmrEvent>(); var decoder=new ProtocolDecoder(48000,events.Add,privacyKeys:Keys(c));
            decoder.Process(Pi(c),0,0,1,"bs_data");
            for(int burst=0;burst<12;burst++)
                if(burst is 1 or 5 or 6) decoder.Erasure(3000+2880*burst,0); else Feed(decoder,c,burst);
            Check(events.Count(e=>e.Type=="erasure")==9);
            foreach(var frame in events.Where(e=>e.Type=="vocoder_frame"))
                Check(Field(frame,"ambe49_hex")==c.Frames[(int)(long)frame.Data["frame_sequence"]!].Plaintext49);
            var context=new DmraArc4Context(c.KeyId,Convert.ToUInt32(c.InitialMi,16),Keys(c));
            for(int n=0;n<36;n++)
            {
                bool valid=n is not (1 or 17 or 18);
                var result=context.Transform(Unpack(c.Frames[n].Ciphertext49),valid);
                Check(valid ? Bits.Hex(result!)==c.Frames[n].Plaintext49 : result==null);
            }
        });
        test("DMRA ARC4 missing/wrong keys, missing PI and unsupported profiles",()=>
        {
            var c=cases[0];
            foreach(var scenario in new[]{"missing_key","unsupported_algorithm","unsupported_vendor","missing_pi","wrong_key"})
            {
                var events=new List<DmrEvent>();
                var keys=scenario=="missing_key" ? Keys(cases[1]) : scenario=="wrong_key" ? new Arc4Keyring(new Dictionary<byte,byte[]> { [c.KeyId]=new byte[5] }) : Keys(c);
                var decoder=new ProtocolDecoder(48000,events.Add,privacyKeys:keys);
                decoder.Process(Burst.MakeLc(Lc(),7),0,0,1,"bs_data");
                if(scenario!="missing_pi") decoder.Process(Pi(c,scenario=="unsupported_algorithm"?0x24:0x21,scenario=="unsupported_vendor"?0x68:0x10),1000,0,1,"bs_data");
                Feed(decoder,c,0);
                var frame=events.Last(e=>e.Type=="vocoder_frame");
                if(scenario=="wrong_key")
                { Check(Field(frame,"decryption_status")=="decrypted_unverified" && Field(frame,"ambe49_hex")!=c.Frames[2].Plaintext49); }
                else
                {
                    Check(frame.Data["ambe49_hex"]==null);
                    Check(Field(frame,"decryption_status")==(scenario.StartsWith("unsupported")?"unsupported_profile":scenario=="missing_pi"?"missing_parameters":"missing_key"));
                }
            }
        });
        test("DMRA ARC4 rejects bad PI, lost phase and clears state on discontinuity",()=>
        {
            var c=cases[0]; var events=new List<DmrEvent>(); var decoder=new ProtocolDecoder(48000,events.Add,privacyKeys:Keys(c));
            decoder.Process(Pi(c),0,0,1,"bs_data"); Feed(decoder,c,0);
            Feed(decoder,c,6); // unexpected A when B was due; no invented keystream position
            Check(Field(events.Last(),"decryption_status")=="alignment_lost" && events.Last().Data["ambe49_hex"]==null);
            decoder.Process(Pi(c),22000,0,1,"bs_data"); Feed(decoder,c,0);
            Check(Field(events.Last(),"ambe49_hex")==c.Frames[2].Plaintext49);
            decoder.Process(Pi(c,valid:false),24000,0,1,"bs_data"); Feed(decoder,c,0);
            Check(Field(events.Last(),"decryption_status")=="invalid_privacy_header" && events.Last().Data["ambe49_hex"]==null);
            decoder.Process(Pi(c),26000,0,1,"bs_data"); decoder.Reset(27000); Feed(decoder,c,0);
            Check(events.Last().Data["ambe49_hex"]==null && events.Last().Data["privacy_key_id"]==null);
            var invalid=new ProtocolDecoder(48000,events.Add,privacyKeys:Keys(c));
            invalid.Process(Pi(c,valid:false),0,0,1,"bs_data"); Feed(invalid,c,0);
            Check(events.Last().Data["ambe49_hex"]==null && events.Last().Data["privacy_key_id"]==null);
        });
        test("Recorded-style IQ to decrypted frames, streaming chunks and JSON fixture",()=>
        {
            var c=cases[0];
            var fixture=Synthetic.Create(replaceBurst:(round,slot,original)=>slot!=0?original:round switch {
                0=>Burst.MakeLc(Lc(),7),1=>Pi(c),>=2 and <14=>Voice(c,round-2),14=>Burst.MakeLc(Lc(),7,true),
                _=>Burst.MakeData(Bits.Unpack(Convert.FromHexString("FF83DF1732094ED1E7CD8A91")),7,9) });
            List<DmrEvent>? first=null;
            foreach(int chunk in new[]{137,8192})
            {
                var output=Synthetic.Decode(fixture.Iq,chunk:chunk,privacyKeys:Keys(c));
                var decrypted=output.Where(e=>e.Type=="vocoder_frame" && Field(e,"decryption_status")=="decrypted_unverified").ToArray();
                Check(decrypted.Length==36,$"Expected 36 decrypted frames, got {decrypted.Length}");
                Check(decrypted.Select(e=>Field(e,"ambe49_hex")).SequenceEqual(c.Frames.Select(f=>f.Plaintext49)));
                if(first!=null) Check(first.Select(e=>e.ToJson()).SequenceEqual(output.Select(e=>e.ToJson())));
                first=output;
            }
            Directory.CreateDirectory(Path.Combine(root,"output"));
            File.WriteAllLines(Path.Combine(root,"output/arc4_synthetic.jsonl"),first!.Select(e=>e.ToJson()));
        });
    }
}

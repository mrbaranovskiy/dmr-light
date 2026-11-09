using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Dmr;
using Dmr.Fec;
using Dmr.Privacy;
using Dmr.Protocol;
using Dmr.Vocoder;

// DMRA AES-128/AES-256, clear filler frames, keyring and the stateless deferred-decryption API.
internal static class PrivacyAesTests
{
    private static void Check(bool ok,string message="Privacy assertion failed") { if(!ok) throw new Exception(message); }
    private static string? Field(DmrEvent e,string key)=>e.Data.GetValueOrDefault(key) as string;
    private static byte[] Lc(int track=0)=>Synthetic.Lc(track==0?123456:654321,track==0?91:92,service:0x40);
    private static byte[] Pi(int algorithm,byte keyId,uint mi,bool valid=true)
    {
        byte[] bytes=[(byte)algorithm,0x10,keyId,0,0,0,0,0,0,91,0,0];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(3,4),mi);
        uint crc=Checks.Crc(Bits.Unpack(bytes[..10]),16,0x1021,finalXor:0xffff)^0x6969u;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10),(ushort)(crc^(valid?0u:1u)));
        return Burst.MakeData(Bits.Unpack(bytes),7,0);
    }
    private static byte[] Unpack49(byte[] packed)=>Bits.Unpack(packed)[..49];

    // Independent reference: IV as the bit stream s[n]=s[n-32]^s[n-22]^s[n-2]^s[n-1] (s[0..31] = MI, MSB first),
    // which is what the C loop's register (taps 31, 21, 1, 0) produces one bit at a time.
    private static byte[] ReferenceIv(uint mi)
    {
        var s=new List<int>(128);
        for(int i=0;i<32;i++) s.Add((int)((mi>>(31-i))&1));
        for(int n=32;n<128;n++) s.Add(s[n-32]^s[n-22]^s[n-2]^s[n-1]);
        var iv=new byte[16];
        for(int i=0;i<128;i++) iv[i/8]|=(byte)(s[i]<<(7-i%8));
        return iv;
    }
    // Independent reference keystream. AES: CFB-128 encryption of zeros is the OFB keystream; first block discarded.
    // ARC4: the RFC 6229-tested class, 256 bytes discarded.
    private static byte[] ReferenceKeystream(DmraAlgorithm algorithm,byte[] key,uint mi)
    {
        if(algorithm==DmraAlgorithm.Arc4)
        {
            byte[] material=[..key,(byte)(mi>>24),(byte)(mi>>16),(byte)(mi>>8),(byte)mi];
            var rc4=new Arc4(material); rc4.Skip(256);
            return Enumerable.Range(0,126).Select(_=>rc4.Next()).ToArray();
        }
        using var aes=Aes.Create(); aes.Key=key;
        return aes.EncryptCfb(new byte[160],ReferenceIv(mi),PaddingMode.None,128)[16..142];
    }
    private static uint ReferenceNextMi(DmraAlgorithm algorithm,uint mi)
    {
        if(algorithm!=DmraAlgorithm.Arc4) return BinaryPrimitives.ReadUInt32BigEndian(ReferenceIv(mi).AsSpan(4));
        for(int n=0;n<32;n++) mi=(mi<<1)|(((mi>>31)^(mi>>3)^(mi>>1))&1);
        return mi;
    }
    private static byte[] Xor49(byte[] packed,byte[] keystream,int frame)
    {
        var result=new byte[7];
        for(int n=0;n<7;n++) result[n]=(byte)(packed[n]^keystream[7*frame+n]);
        result[6]&=0x80; return result;
    }
    private static readonly byte[] Silence=Convert.FromHexString("F801A99F8CE080");

    private sealed record Stream(DmraAlgorithm Algorithm,byte KeyId,byte[] Key,uint Mi,byte[][] Clear,byte[][] Cipher,uint[] FrameMi);
    // 36 frames (two superframes). Frame 4 is the silence vector and frame 23 has bits 24..43 zero: sent clear.
    private static Stream MakeStream(DmraAlgorithm algorithm,byte keyId,uint mi,int seed)
    {
        var rng=new Random(seed); var key=new byte[DmraPrivacy.KeyLength(algorithm)]; rng.NextBytes(key);
        var clear=new byte[36][]; var cipher=new byte[36][]; var frameMi=new uint[36]; uint current=mi; byte[] ks=[];
        for(int n=0;n<36;n++)
        {
            if(n%18==0) { if(n>0) current=ReferenceNextMi(algorithm,current); ks=ReferenceKeystream(algorithm,key,current); }
            frameMi[n]=current;
            var frame=new byte[7]; rng.NextBytes(frame); frame[6]&=0x80;
            if(n==4) frame=(byte[])Silence.Clone();
            if(n==23) { frame[3]=0; frame[4]=0; frame[5]&=0x0F; }
            clear[n]=frame;
            cipher[n]=DmraPrivacy.IsClearFiller(frame) ? (byte[])frame.Clone() : Xor49(frame,ks,n%18);
            Check(DmraPrivacy.IsClearFiller(frame)==(n is 4 or 23),"Unexpected filler in generated stream");
            Check(DmraPrivacy.IsClearFiller(cipher[n])==(n is 4 or 23),"Generated ciphertext looks like filler");
        }
        return new(algorithm,keyId,key,mi,clear,cipher,frameMi);
    }
    private static byte[] Voice(Stream s,int burst,int track=0)=>Synthetic.Voice(Lc(track),burst%6,7,
        s.Cipher.Skip(burst*3).Take(3).SelectMany(f=>Ambe.Encode(Unpack49(f))).ToArray());
    private static int AlgorithmId(DmraAlgorithm a)=>a switch { DmraAlgorithm.Arc4=>0x21,DmraAlgorithm.Aes128=>0x24,_=>0x25 };
    private static PrivacyKeyring Ring(Stream s,byte[]? key=null)=>s.Algorithm switch {
        DmraAlgorithm.Arc4=>new PrivacyKeyring(arc4:new Dictionary<byte,byte[]>{[s.KeyId]=key??s.Key}),
        DmraAlgorithm.Aes128=>new PrivacyKeyring(aes128:new Dictionary<byte,byte[]>{[s.KeyId]=key??s.Key}),
        _=>new PrivacyKeyring(aes256:new Dictionary<byte,byte[]>{[s.KeyId]=key??s.Key}) };
    private static List<DmrEvent> DecodeIq(Stream s,PrivacyKeyring? keys,int chunk=4096)
    {
        var fixture=Synthetic.Create(replaceBurst:(round,slot,original)=>slot!=0?original:round switch {
            0=>Burst.MakeLc(Lc(),7),1=>Pi(AlgorithmId(s.Algorithm),s.KeyId,s.Mi),>=2 and <14=>Voice(s,round-2),14=>Burst.MakeLc(Lc(),7,true),
            _=>Burst.MakeData(Bits.Unpack(Convert.FromHexString("FF83DF1732094ED1E7CD8A91")),7,9) });
        return Synthetic.Decode(fixture.Iq,chunk:chunk,privacyKeys:keys);
    }
    private static byte[] Deferred(DmraAlgorithm algorithm,byte[] key,DmrEvent frame)
    {
        var keystream=new byte[126];
        DmraPrivacy.SuperframeKeystream(algorithm,key,Convert.ToUInt32(Field(frame,"privacy_message_indicator")!,16),keystream);
        var clear=new byte[7];
        DmraPrivacy.DecryptFrame(keystream,(int)frame.Data["privacy_frame_index"]!,Convert.FromHexString(Field(frame,"cipher49_hex")!),clear);
        return clear;
    }

    public static void Register(Action<string,Action> test,string root)
    {
        test("AES-OFB keystream: NIST SP 800-38A F.4.1/F.4.5 and CFB composition",()=>
        {
            byte[] iv=Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
            byte[] plain=Convert.FromHexString("6BC1BEE22E409F96E93D7E117393172AAE2D8A571E03AC9C9EB76FAC45AF8E5130C81C46A35CE411E5FBC1191A0A52EFF69F2445DF4F9B17AD2B417BE66C3710");
            foreach(var (key,cipher) in new[]{
                ("2B7E151628AED2A6ABF7158809CF4F3C","3B3FD92EB72DAD20333449F8E83CFB4A7789508D16918F03F53C52DAC54ED8259740051E9C5FECF64344F7A82260EDCC304C6528F659C77866A510D9C1D6AE5E"),
                ("603DEB1015CA71BE2B73AEF0857D77811F352C073B6108D72D9810A30914DFF4","DC7E84BFDA79164B7ECD8486985D38604FEBDC6740D20B3AC88F6AD82A4FB08D71AB47A086E86EEDF39D1C5BBA97C4080126141D67F37BE8538F5A8BE740E484")})
            {
                using var aes=DmraPrivacy.CreateAes(Convert.FromHexString(key));
                var stream=new byte[64]; DmraPrivacy.AesOfb(aes,iv,stream);
                var expected=Convert.FromHexString(cipher).Zip(plain,(c,p)=>(byte)(c^p)).ToArray();
                Check(stream.SequenceEqual(expected),$"OFB output blocks differ for a {key.Length*4}-bit key");
                using var check=Aes.Create(); check.Key=Convert.FromHexString(key);
                var longer=new byte[160]; DmraPrivacy.AesOfb(aes,iv,longer);
                Check(longer.SequenceEqual(check.EncryptCfb(new byte[160],iv,PaddingMode.None,128)),"OFB differs from CFB-of-zeros composition");
            }
        });
        test("DMRA AES IV expansion (taps 31, 21, 1, 0) and next MI against bit-by-bit reference",()=>
        {
            // Hand-checked: MI 00000001 gives output bits 1, (0,1,1)x7 until the x^22 tap reaches the initial 1 (bit 22):
            // B6 DB 68 A3 ...; the remaining 64 bits were checked against two independent derivations.
            var iv=new byte[16];
            Check(DmraPrivacy.ExpandIv(1,iv)==0xB6DB68A3);
            Check(Convert.ToHexString(iv)=="00000001B6DB68A3CF213212B93A9645","IV for MI 00000001");
            Check(Convert.ToHexString(ReferenceIv(1))=="00000001B6DB68A3CF213212B93A9645","Test reference IV for MI 00000001");
            foreach(uint mi in new uint[]{1,0xDEADBEEF,0xFFFFFFFF,0,0x12345678,0x80000000})
            {
                uint next=DmraPrivacy.ExpandIv(mi,iv);
                Check(iv.SequenceEqual(ReferenceIv(mi)),$"IV differs for MI {mi:X8}");
                Check(next==ReferenceNextMi(DmraAlgorithm.Aes128,mi) && next==DmraPrivacy.NextMessageIndicator(DmraAlgorithm.Aes256,mi));
                Check(DmraPrivacy.NextMessageIndicator(DmraAlgorithm.Aes128,mi)==next);
                Check(DmraPrivacy.NextMessageIndicator(DmraAlgorithm.Arc4,mi)==ReferenceNextMi(DmraAlgorithm.Arc4,mi));
            }
            Check(Convert.ToHexString(ReferenceIv(0xDEADBEEF))=="DEADBEEF3B1ED802B693C3CD217D29D6");
            Check(Convert.ToHexString(ReferenceIv(0xFFFFFFFF))=="FFFFFFFF6DB6D861451F11F19716723C");
        });
        test("DMRA superframe keystream for ARC4, AES-128, AES-256 against independent composition",()=>
        {
            var rng=new Random(91);
            foreach(var algorithm in new[]{DmraAlgorithm.Arc4,DmraAlgorithm.Aes128,DmraAlgorithm.Aes256})
                foreach(uint start in new uint[]{1,0xDEADBEEF,0xFFFFFFFF})
                {
                    var key=new byte[DmraPrivacy.KeyLength(algorithm)]; rng.NextBytes(key); uint mi=start;
                    for(int superframe=0;superframe<3;superframe++)
                    {
                        var keystream=new byte[126]; DmraPrivacy.SuperframeKeystream(algorithm,key,mi,keystream);
                        Check(keystream.SequenceEqual(ReferenceKeystream(algorithm,key,mi)),$"{algorithm} keystream differs, MI {mi:X8}");
                        mi=DmraPrivacy.NextMessageIndicator(algorithm,mi);
                    }
                }
        });
        test("DMRA algorithm IDs, key lengths and argument validation without key disclosure",()=>
        {
            foreach(var (id,expected) in new (int,DmraAlgorithm?)[]{(0x21,DmraAlgorithm.Arc4),(0x01,DmraAlgorithm.Arc4),(0x24,DmraAlgorithm.Aes128),
                (0x04,DmraAlgorithm.Aes128),(0x25,DmraAlgorithm.Aes256),(0x05,DmraAlgorithm.Aes256),(0x22,null),(0x02,null),(0x23,null),
                (0x26,null),(0x27,null),(0x00,null),(0x80,null),(0x31,null),(-1,null),(256,null)})
                Check(DmraPrivacy.AlgorithmFor(id)==expected,$"Algorithm ID {id:X2}");
            Check(DmraPrivacy.KeyLength(DmraAlgorithm.Arc4)==5 && DmraPrivacy.KeyLength(DmraAlgorithm.Aes128)==16 && DmraPrivacy.KeyLength(DmraAlgorithm.Aes256)==32);
            byte[] secret=Convert.FromHexString("5EC2E75EC2E75EC2E75EC2E75EC2E75EC2E7");
            foreach(var algorithm in new[]{DmraAlgorithm.Arc4,DmraAlgorithm.Aes128,DmraAlgorithm.Aes256})
            {
                try { DmraPrivacy.SuperframeKeystream(algorithm,secret,1,new byte[126]); throw new Exception("Wrong key length accepted"); }
                catch(ArgumentException e) { Check(!e.ToString().Contains("5EC2E7",StringComparison.OrdinalIgnoreCase) && !e.ToString().Contains("94")); }
            }
            bool Throws(Action a) { try { a(); return false; } catch(ArgumentException) { return true; } }
            Check(Throws(()=>DmraPrivacy.SuperframeKeystream(DmraAlgorithm.Arc4,new byte[5],1,new byte[125])));
            Check(Throws(()=>DmraPrivacy.DecryptFrame(new byte[126],18,new byte[7],new byte[7])));
            Check(Throws(()=>DmraPrivacy.DecryptFrame(new byte[126],-1,new byte[7],new byte[7])));
            Check(Throws(()=>DmraPrivacy.DecryptFrame(new byte[126],0,new byte[6],new byte[7])));
            Check(Throws(()=>DmraPrivacy.IsClearFiller(new byte[8])));
            Check(Throws(()=>new PrivacyKeyring(aes128:new Dictionary<byte,byte[]>{[1]=new byte[32]})));
            Check(Throws(()=>new PrivacyKeyring(aes256:new Dictionary<byte,byte[]>{[1]=new byte[16]})));
        });
        test("Privacy key file with ARC4, AES-128 and AES-256 keys; no key disclosure",()=>
        {
            string a128=string.Concat(Enumerable.Repeat("0123456789ABCDEF",2)), a256=string.Concat(Enumerable.Repeat("FEDCBA9876543210",4));
            string json=$"{{\"dmra_arc4\":[{{\"key_id\":7,\"key_hex\":\"0102030405\"}}],\"dmra_aes\":[{{\"key_id\":1,\"key_hex\":\"{a128}\"}},{{\"key_id\":1,\"key_hex\":\"{a256}\"}},{{\"key_id\":2,\"key_hex\":\"{a128}\"}}]}}";
            var ring=PrivacyKeyring.FromJson(json);
            Check(Convert.ToHexString(ring.Find(DmraAlgorithm.Arc4,7)!)=="0102030405");
            Check(Convert.ToHexString(ring.Find(DmraAlgorithm.Aes128,1)!)==a128 && Convert.ToHexString(ring.Find(DmraAlgorithm.Aes256,1)!)==a256);
            Check(ring.Contains(DmraAlgorithm.Aes128,2) && !ring.Contains(DmraAlgorithm.Aes256,2) && !ring.Contains(DmraAlgorithm.Arc4,1));
            Check(ring.KeyIds(DmraAlgorithm.Aes128).SequenceEqual(new byte[]{1,2}));
            Check(PrivacyKeyring.FromJson("{\"dmra_aes\":[]}").KeyIds(DmraAlgorithm.Aes128).Count==0);
            Check(PrivacyKeyring.FromJson("{\"dmra_arc4\":[{\"key_id\":7,\"key_hex\":\"0102030405\"}]}").Contains(DmraAlgorithm.Arc4,7));
            foreach(var bad in new[]{json.Replace(a128,a128[..30]),json.Replace(a256,"secret"+a256[6..]),json.Replace("\"key_id\":2","\"key_id\":1"),
                json.Replace("dmra_aes","dmra_des"),json.Replace("0102030405",a128),"[]",$"{{\"dmra_aes\":[{{\"key_id\":1,\"key_hex\":\"{a128}\",\"x\":1}}]}}"})
            {
                try { PrivacyKeyring.FromJson(bad); throw new Exception("Invalid key file accepted"); }
                catch(ArgumentException ex) { Check(!ex.ToString().Contains("secret") && !ex.ToString().Contains("0123456789") && !ex.ToString().Contains("FEDCBA")); }
            }
            // The ARC4-only keyring keeps its strict format and is still a PrivacyKeyring.
            PrivacyKeyring legacy=Arc4Keyring.FromJson("{\"dmra_arc4\":[{\"key_id\":7,\"key_hex\":\"0102030405\"}]}");
            Check(legacy.Contains(DmraAlgorithm.Arc4,7));
            try { Arc4Keyring.FromJson(json); throw new Exception("AES section accepted by the ARC4 keyring"); } catch(ArgumentException) { }
        });
        test("Clear filler frames (silence, bits 24..43 zero) are not XORed and consume their position: ARC4 and AES",()=>
        {
            Check(DmraPrivacy.IsClearFiller(Silence) && DmraPrivacy.IsClearFiller(Convert.FromHexString("F801A99F8CE0FF")));
            Check(!DmraPrivacy.IsClearFiller(Convert.FromHexString("F801A99F8CE000")),"Silence differs in bit 48");
            Check(DmraPrivacy.IsClearFiller(Convert.FromHexString("FFFFFF00000FFF")) && DmraPrivacy.IsClearFiller(new byte[7]));
            Check(!DmraPrivacy.IsClearFiller(Convert.FromHexString("FFFFFF00001FFF")),"Bit 43 set");
            Check(!DmraPrivacy.IsClearFiller(Convert.FromHexString("FFFFFF80000FFF")),"Bit 24 set");
            foreach(var algorithm in new[]{DmraAlgorithm.Arc4,DmraAlgorithm.Aes128,DmraAlgorithm.Aes256})
            {
                var s=MakeStream(algorithm,3,0x0BADF00D,17);
                var context=new DmraPrivacyContext(algorithm,s.KeyId,s.Mi,Ring(s));
                for(int n=0;n<36;n++)
                {
                    if(n%3==0) context.AlignBurst(n/3%6,n%18==0);
                    Check(context.MessageIndicator==s.FrameMi[n] && context.FrameIndex==n%18);
                    var clear=context.Transform(Unpack49(s.Cipher[n]),true)!;
                    Check(clear.SequenceEqual(s.Clear[n]),$"{algorithm} frame {n}");
                    if(n is 4 or 23) Check(clear.SequenceEqual(s.Cipher[n]),"Filler was XORed");
                }
                context.Dispose();
            }
        });
        foreach(var algorithm in new[]{DmraAlgorithm.Aes256,DmraAlgorithm.Aes128})
            test($"DMRA {algorithm} synthetic IQ: live with key, deferred API without key, wrong key",()=>
            {
                var s=MakeStream(algorithm,(byte)(algorithm==DmraAlgorithm.Aes128?1:9),algorithm==DmraAlgorithm.Aes128?0xDEADBEEFu:0x00000001u,algorithm==DmraAlgorithm.Aes128?5:6);
                List<string>? first=null;
                foreach(int chunk in new[]{137,8192})
                {
                    var keyed=DecodeIq(s,Ring(s),chunk).Where(e=>e.Type=="vocoder_frame" && e.Slot==1).ToArray();
                    Check(keyed.Length==36,$"Expected 36 frames, got {keyed.Length}");
                    for(int n=0;n<36;n++)
                    {
                        Check(Field(keyed[n],"decryption_status")=="decrypted_unverified" && Field(keyed[n],"ambe49_status")=="decrypted_unverified");
                        Check(Field(keyed[n],"ambe49_hex")==Convert.ToHexString(s.Clear[n]),$"Live {algorithm} frame {n}");
                        Check(Field(keyed[n],"cipher49_hex")==Convert.ToHexString(s.Cipher[n]));
                        Check(Equals(keyed[n].Data["privacy_frame_index"],n%18) && Field(keyed[n],"privacy_message_indicator")==s.FrameMi[n].ToString("X8"));
                        Check(Field(keyed[n],"privacy_profile")==DmraPrivacy.ProfileName(algorithm) && Equals(keyed[n].Data["privacy_algorithm_id"],AlgorithmId(algorithm)));
                    }
                    var json=keyed.Select(e=>e.ToJson()).ToList();
                    Check(!string.Join("\n",json).Contains(Convert.ToHexString(s.Key)),"Key material in output");
                    if(first!=null) Check(first.SequenceEqual(json),"Chunking changed output");
                    first=json;
                }
                var all=DecodeIq(s,null);
                var keyless=all.Where(e=>e.Type=="vocoder_frame" && e.Slot==1).ToArray(); Check(keyless.Length==36);
                for(int n=0;n<36;n++)
                {
                    Check(keyless[n].Data["ambe49_hex"]==null && Field(keyless[n],"decryption_status")=="missing_key" && Field(keyless[n],"ambe49_status")=="opaque_privacy");
                    Check(Field(keyless[n],"cipher49_hex")==Convert.ToHexString(s.Cipher[n]));
                    Check(Deferred(algorithm,s.Key,keyless[n]).SequenceEqual(s.Clear[n]),$"Deferred {algorithm} frame {n}");
                }
                Directory.CreateDirectory(Path.Combine(root,"output"));
                File.WriteAllLines(Path.Combine(root,$"output/{DmraPrivacy.ProfileName(algorithm)}_synthetic_nokey.jsonl"),all.Select(e=>e.ToJson()));
                var wrongKey=(byte[])s.Key.Clone(); wrongKey[0]^=1;
                var wrong=DecodeIq(s,Ring(s,wrongKey)).Where(e=>e.Type=="vocoder_frame" && e.Slot==1).ToArray();
                Check(wrong.All(e=>Field(e,"decryption_status")=="decrypted_unverified"));
                for(int n=0;n<36;n++)
                    Check(n is 4 or 23 ? Field(wrong[n],"ambe49_hex")==Convert.ToHexString(s.Clear[n]) : Field(wrong[n],"ambe49_hex")!=Convert.ToHexString(s.Clear[n]),$"Wrong key frame {n}");
            });
        test("DMRA AES alignment loss, erasures and DES/unsupported profiles",()=>
        {
            var s=MakeStream(DmraAlgorithm.Aes256,4,0xCAFEF00D,8);
            var events=new List<DmrEvent>(); var decoder=new ProtocolDecoder(48000,events.Add,privacyKeys:Ring(s));
            decoder.Process(Pi(0x25,s.KeyId,s.Mi),0,0,1,"bs_data");
            for(int burst=0;burst<12;burst++)
                if(burst is 1 or 5 or 6) decoder.Erasure(3000+2880*burst,0);
                else decoder.Process(Voice(s,burst),3000+burst*2880,0,1,burst%6==0?"bs_voice":"embedded");
            foreach(var frame in events.Where(e=>e.Type=="vocoder_frame"))
            {
                int n=(int)(long)frame.Data["frame_sequence"]!;
                Check(Field(frame,"ambe49_hex")==Convert.ToHexString(s.Clear[n]) && Equals(frame.Data["privacy_frame_index"],n%18),$"Frame {n} after erasures");
            }
            Check(events.Count(e=>e.Type=="vocoder_frame")==27);
            // Unexpected phase: alignment lost, position and MI no longer reported, ciphertext still is.
            decoder.Process(Pi(0x05,s.KeyId,s.Mi),50000,0,1,"bs_data");
            decoder.Process(Voice(s,0),53000,0,1,"bs_voice"); decoder.Process(Voice(s,6),56000,0,1,"bs_voice");
            var lost=events.Last(); Check(Field(lost,"decryption_status")=="alignment_lost" && lost.Data["privacy_frame_index"]==null && lost.Data["privacy_message_indicator"]==null);
            Check(Field(lost,"cipher49_hex")==Convert.ToHexString(s.Cipher[20]) && Field(lost,"privacy_profile")=="dmra_aes256");
            foreach(int alg in new[]{0x22,0x02,0x26})
            {
                var other=new List<DmrEvent>(); var d=new ProtocolDecoder(48000,other.Add,privacyKeys:Ring(s));
                d.Process(Pi(alg,s.KeyId,s.Mi),0,0,1,"bs_data"); d.Process(Voice(s,0),3000,0,1,"bs_voice");
                var f=other.Last();
                Check(Field(f,"decryption_status")=="unsupported_profile" && f.Data["ambe49_hex"]==null && f.Data["privacy_frame_index"]==null);
                Check(Field(f,"privacy_profile")==(alg==0x26?null:"dmra_des") && Field(f,"cipher49_hex")==Convert.ToHexString(s.Cipher[2]));
            }
        });
        test("DMRA FID 0x10 group/private voice LC uses the standard address layout; other vendor FIDs stay opaque",()=>
        {
            var events=new List<DmrEvent>(); var decoder=new ProtocolDecoder(48000,events.Add);
            // Radio-observed AnyTone LC: FID 0x10, FLCO 0, service options 0x40 (privacy), group 1, source 0x0005D0.
            decoder.Process(Burst.MakeLc(Convert.FromHexString("0010400000010005D0"),1),0,0,1,"bs_data");
            var update=events.Last(e=>e.Type=="call_update");
            Check(Equals(update.Data["source_id"],0x5D0) && Equals(update.Data["destination_id"],1) && Equals(update.Data["call_type"],"group"));
            Check(Equals(update.Data["fid"],0x10) && Equals(update.Data["flco"],0) && Equals(update.Data["privacy_status"],"indicated"));
            Check(Equals(update.Data["provenance"],"voice_lc_header") && !update.Data.ContainsKey("interpretation"));
            decoder.Process(Burst.MakeLc(Convert.FromHexString("0310000004D2000929"),1),3000,1,2,"bs_data");
            update=events.Last(e=>e.Type=="call_update");
            Check(Equals(update.Data["source_id"],0x929) && Equals(update.Data["destination_id"],0x4D2) && Equals(update.Data["call_type"],"private"));
            Check(Equals(update.Data["privacy_status"],"not_indicated") && Equals(update.Data["fid"],0x10));
            foreach(var raw in new[]{"0410400000010005D0","0068400000010005D0","8010400000010005D0"})
            {
                var other=new List<DmrEvent>(); var d=new ProtocolDecoder(48000,other.Add);
                d.Process(Burst.MakeLc(Convert.FromHexString(raw),1),0,0,1,"bs_data");
                var u=other.Last(e=>e.Type=="call_update");
                Check(Equals(u.Data["interpretation"],"opaque_feature_set") && u.Data["source_id"]==null,$"LC {raw} should stay opaque");
            }
        });
        test("Real radio ARC4 capture (AnyTone, tests/fixtures/arc4_anytone_1488to1.cs16): live == deferred, every frame decrypted",()=>
        {
            // The fixture: a 6.2 s AnyTone call recorded by the radio's owner, published with its (test) key by the owner.
            // DMR_ARC4_CAPTURE / DMR_ARC4_KEY run the same check on another capture: raw cs16/cs32/cu8/cf32 by extension, or
            // WAV; DMR_ARC4_CAPTURE_RATE (default 50000) and DMR_ARC4_CAPTURE_OFFSET (Hz, default 0) for raw input.
            string? given=Environment.GetEnvironmentVariable("DMR_ARC4_CAPTURE"), keyHex=Environment.GetEnvironmentVariable("DMR_ARC4_KEY");
            bool fixture=string.IsNullOrEmpty(given);
            string path=fixture ? Path.Combine(root,"tests/fixtures/arc4_anytone_1488to1.cs16") : given!;
            if(fixture) keyHex="0000002806";
            if(string.IsNullOrEmpty(keyHex)) { Console.WriteLine("     skipped: DMR_ARC4_KEY not set for DMR_ARC4_CAPTURE"); return; }
            var key=Convert.FromHexString(keyHex); Check(key.Length==5,"DMR_ARC4_KEY must be 10 hex digits");
            string format=Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            int rate=int.Parse(Environment.GetEnvironmentVariable("DMR_ARC4_CAPTURE_RATE") ?? "50000");
            double offset=double.Parse(Environment.GetEnvironmentVariable("DMR_ARC4_CAPTURE_OFFSET") ?? "0",System.Globalization.CultureInfo.InvariantCulture);
            List<DmrEvent> Run(PrivacyKeyring? keys)
            {
                using var reader=format=="wav" ? new Dmr.Input.IqReader(path) : new Dmr.Input.IqReader(path,format,rate);
                var events=new List<DmrEvent>(); var rx=new DmrReceiver(new(reader.SampleRate,offset,PrivacyKeys:keys),events.Add);
                var buffer=new System.Numerics.Complex[4096]; int n; while((n=reader.Read(buffer))>0) rx.Push(buffer.AsSpan(0,n));
                rx.Complete(); return events.Where(e=>e.Type=="vocoder_frame").ToList();
            }
            var keyless=Run(null); Check(keyless.Count>0,"No voice frames");
            var ids=keyless.Select(e=>e.Data["privacy_key_id"]).Where(x=>x!=null).Distinct().ToArray(); Check(ids.Length==1,"Expected one PI key ID");
            var live=Run(new PrivacyKeyring(arc4:new Dictionary<byte,byte[]>{[(byte)(int)ids[0]!]=key}));
            Check(live.Count==keyless.Count);
            int decrypted=0;
            for(int n=0;n<live.Count;n++)
            {
                if(Field(live[n],"decryption_status")!="decrypted_unverified") continue;
                decrypted++;
                Check(Field(keyless[n],"decryption_status")=="missing_key" && keyless[n].Data["privacy_frame_index"]!=null);
                Check(Field(keyless[n],"cipher49_hex")==Field(live[n],"cipher49_hex"));
                Check(Convert.ToHexString(Deferred(DmraAlgorithm.Arc4,key,keyless[n]))==Field(live[n],"ambe49_hex"),$"Deferred differs at frame {n}");
            }
            Console.WriteLine($"     real capture: {live.Count} frames, {decrypted} decrypted, profile {Field(live[0],"privacy_profile")}, first MI {Field(keyless[0],"privacy_message_indicator")}");
            Check(decrypted==live.Count,$"Only {decrypted} of {live.Count} frames decrypted");
            Check(!string.Concat(live.Select(e=>e.ToJson())).Contains(keyHex,StringComparison.OrdinalIgnoreCase),"Key in output");
            if(fixture)
            {
                Check(live.Count==234 && Field(keyless[0],"privacy_message_indicator")=="7BC5215F" && Field(keyless[0],"cipher49_hex")=="5FFD25EC03FD00",
                    "The fixture's known content changed");
                Check(Equals(live[0].Data["source_id"],1488) && Equals(live[0].Data["destination_id"],1),"FID 0x10 LC: 1488 -> 1");
            }
        });
        test("Deferred API equals live ARC4 output (108-vector keys) and keyless ARC4 context",()=>
        {
            using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/fixtures/arc4_reference.json")));
            foreach(var c in document.RootElement.GetProperty("cases").EnumerateArray())
            {
                byte keyId=c.GetProperty("key_id").GetByte(); var key=Convert.FromHexString(c.GetProperty("key_hex").GetString()!);
                uint mi=Convert.ToUInt32(c.GetProperty("initial_mi").GetString()!,16);
                var cipher=c.GetProperty("frames").EnumerateArray().Select(f=>Convert.FromHexString(f.GetProperty("ciphertext49").GetString()!)).ToArray();
                var plain=c.GetProperty("frames").EnumerateArray().Select(f=>f.GetProperty("plaintext49").GetString()!).ToArray();
                var s=new Stream(DmraAlgorithm.Arc4,keyId,key,mi,[],cipher,[]);
                List<DmrEvent> Run(PrivacyKeyring? keys)
                {
                    var events=new List<DmrEvent>(); var decoder=new ProtocolDecoder(48000,events.Add,privacyKeys:keys);
                    decoder.Process(Burst.MakeLc(Lc(),7),0,0,1,"bs_data"); decoder.Process(Pi(0x21,keyId,mi),1000,0,1,"bs_data");
                    for(int burst=0;burst<12;burst++) decoder.Process(Voice(s,burst),3000+burst*2880,0,1,burst%6==0?"bs_voice":"embedded");
                    return events.Where(e=>e.Type=="vocoder_frame").ToList();
                }
                var live=Run(Ring(s)); var keyless=Run(null);
                Check(live.Count==36 && keyless.Count==36);
                for(int n=0;n<36;n++)
                {
                    Check(Field(live[n],"ambe49_hex")==plain[n]);
                    Check(Field(keyless[n],"decryption_status")=="missing_key" && Field(keyless[n],"privacy_profile")=="dmra_arc4");
                    Check(Convert.ToHexString(Deferred(DmraAlgorithm.Arc4,key,keyless[n]))==Field(live[n],"ambe49_hex"),$"ARC4 deferred frame {n}");
                    Check(Convert.ToHexString(Deferred(DmraAlgorithm.Arc4,key,live[n]))==plain[n]);
                }
            }
        });
    }
}

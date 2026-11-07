using System.Text;
using Dmr;
using Dmr.Fec;
using Dmr.Protocol;
using Dmr.Vocoder;

/// <summary>Talker alias, GPS LC, data blocks (rate 1/2, 3/4, 1), packet reassembly and text applications, all with
/// vectors built from the ETSI layouts (TS 102 361-1 clause 8.2 and Annex B, TS 102 361-2 talker alias and GPS LCs).</summary>
internal static class MetadataTests
{
    private static void Check(bool ok,string message="Metadata assertion failed") { if(!ok) throw new Exception(message); }
    private static readonly byte[] Socket=Enumerable.Repeat(Ambe.Encode(new byte[49]),3).SelectMany(x=>x).ToArray();

    // ---- Talker alias and GPS LC builders (TS 102 361-2) ----
    /// <summary>Four 9-octet LCs: header (FLCO 4) and blocks 1-3 (FLCO 5-7), FID 0.</summary>
    public static byte[][] TalkerAlias(string text,int format,int? declared=null)
    {
        var bits=new List<byte>();
        byte[] payload; int length;
        switch(format)
        {
            case 0: payload=Encoding.ASCII.GetBytes(text); length=text.Length; break;
            case 1: payload=Encoding.Latin1.GetBytes(text); length=payload.Length; break;
            case 2: payload=Encoding.UTF8.GetBytes(text); length=payload.Length; break;
            default: payload=Encoding.BigEndianUnicode.GetBytes(text); length=payload.Length/2; break;
        }
        length=declared ?? length;
        bits.AddRange(Bits.FromUInt((uint)format,2)); bits.AddRange(Bits.FromUInt((uint)length,5));
        if(format==0) foreach(byte c in payload) bits.AddRange(Bits.FromUInt(c,7));
        else { bits.Add(0); foreach(byte c in payload) bits.AddRange(Bits.FromUInt(c,8)); }
        Check(bits.Count<=224,"Alias too long for the vector");
        while(bits.Count<224) bits.Add(0);
        var data=Bits.Pack(bits.ToArray());
        return Enumerable.Range(0,4).Select(i=>(byte[])[(byte)(4+i),0,..data.AsSpan(i*7,7)]).ToArray();
    }
    public static byte[] Gps(double latitude,double longitude,int error)
    {
        int lat=(int)Math.Round(latitude*(1<<24)/180.0)&0xFFFFFF, lon=(int)Math.Round(longitude*(1<<25)/360.0)&0x1FFFFFF;
        return [8,0,(byte)((error<<1)|(lon>>24)),(byte)(lon>>16),(byte)(lon>>8),(byte)lon,(byte)(lat>>16),(byte)(lat>>8),(byte)lat];
    }
    private sealed class Call
    {
        public readonly List<DmrEvent> Events=new(); public readonly ProtocolDecoder Decoder; public long Sample=1000;
        public Call() { Decoder=new(48000,Events.Add); }
        public void Header(byte[] lc) { Decoder.Process(Burst.MakeLc(lc,7),Sample,0,1,"bs_data"); Sample+=2880; }
        public void Terminator(byte[] lc) { Decoder.Process(Burst.MakeLc(lc,7,true),Sample,0,1,"bs_data"); Sample+=2880; }
        /// <summary>One voice superframe A-F whose embedded LC (bursts B-E) is <paramref name="lc"/>; null = B-E lost.</summary>
        public void Superframe(byte[]? lc)
        {
            for(int phase=0;phase<6;phase++)
            {
                if(lc==null && phase is >=1 and <=4) Decoder.Erasure(Sample,0);
                else Decoder.Process(Synthetic.Voice(lc ?? Synthetic.Lc(),phase,7,Socket),Sample,0,1,phase==0?"bs_voice":"embedded");
                Sample+=2880;
            }
        }
        public DmrEvent[] Of(string type)=>Events.Where(e=>e.Type==type).ToArray();
    }

    // ---- Data packet builders (TS 102 361-1 clause 8.2, B.3.9, B.3.10) ----
    private static byte[] HeaderBurst(byte[] header10)
    {
        uint crc=Checks.Crc(Bits.Unpack(header10),16,0x1021,finalXor:0xffff)^0xCCCCu;
        return Burst.MakeData(Bits.Unpack([..header10,(byte)(crc>>8),(byte)crc]),7,6);
    }
    private static byte[] BlockBurst(byte[] block,int type) => type switch
    {
        7 => Burst.MakeData(Bits.Unpack(block),7,7),
        8 => Burst.MakeInfo(Trellis.Encode34(Bits.Unpack(block)),7,8),
        _ => Burst.MakeInfo(Trellis.EncodeRate1(Bits.Unpack(block)),7,10)
    };
    private static int Octets(int type,bool confirmed)=>(type==7?12:type==8?18:24)-(confirmed?2:0);
    private static void WriteCrc32(byte[] payload)
    {
        uint crc=Checks.DataCrc32(payload.AsSpan(0,payload.Length-4));
        for(int i=0;i<4;i++) payload[payload.Length-4+i]=(byte)(crc>>(8*i));
    }
    /// <summary>Bursts of an unconfirmed (DPF 0010) or confirmed (DPF 0011) packet. <paramref name="corrupt"/> flips a user
    /// octet after the CRCs were computed.</summary>
    public static List<byte[]> Packet(byte[] user,int type,bool confirmed,int sap=4,int source=2001,int destination=1,bool group=false,bool corrupt=false)
    {
        int per=Octets(type,confirmed), blocks=(user.Length+4+per-1)/per, pad=blocks*per-user.Length-4;
        var payload=new byte[blocks*per]; user.CopyTo(payload,0); WriteCrc32(payload);
        byte[] header=[(byte)((group?0x80:0)|((pad>>4)<<4)|(confirmed?3:2)),(byte)((sap<<4)|(pad&15)),
            (byte)(destination>>16),(byte)(destination>>8),(byte)destination,(byte)(source>>16),(byte)(source>>8),(byte)source,
            (byte)(0x80|blocks),(byte)(confirmed?8:0)];
        var bursts=new List<byte[]> { HeaderBurst(header) };
        for(int i=0;i<blocks;i++)
        {
            var data=payload.AsSpan(i*per,per).ToArray();
            byte[] block=data;
            if(confirmed)
            {
                int crc=Checks.Crc9(data,i)^(type==7?0x0F0:type==8?0x1FF:0x10F);
                block=[(byte)((i<<1)|(crc>>8)),(byte)crc,..data];
            }
            if(corrupt && i==0) block[confirmed?3:1]^=0x40;
            bursts.Add(BlockBurst(block,type));
        }
        return bursts;
    }
    /// <summary>Defined short data (DPF 1101, SAP 1010) with rate 1/2 unconfirmed blocks and bit padding.</summary>
    public static List<byte[]> DefinedShortData(byte[] user,int userBits,int dd,int source=2001,int destination=1)
    {
        int blocks=(userBits+32+95)/96, padBits=blocks*96-32-userBits;
        var payload=new byte[blocks*12]; user.AsSpan(0,(userBits+7)/8).CopyTo(payload); WriteCrc32(payload);
        byte[] header=[(byte)(((blocks>>4)&3)<<4|13),(byte)((10<<4)|(blocks&15)),
            (byte)(destination>>16),(byte)(destination>>8),(byte)destination,(byte)(source>>16),(byte)(source>>8),(byte)source,
            (byte)((dd<<2)|1),(byte)padBits];
        var bursts=new List<byte[]> { HeaderBurst(header) };
        for(int i=0;i<blocks;i++) bursts.Add(BlockBurst(payload.AsSpan(i*12,12).ToArray(),7));
        return bursts;
    }
    public static byte[] Udp(byte[] payload,int port,int sourcePort=0)
    {
        int total=28+payload.Length;
        var p=new byte[total];
        byte[] ip=[0x45,0,(byte)(total>>8),(byte)total,0,1,0,0,64,17,0,0,12,0,7,209,12,0,0,1];
        uint sum=0; for(int i=0;i<20;i+=2) sum+=(uint)((ip[i]<<8)|ip[i+1]);
        while(sum>0xFFFF) sum=(sum&0xFFFF)+(sum>>16);
        ip[10]=(byte)(~sum>>8); ip[11]=(byte)~sum;
        ip.CopyTo(p,0);
        sourcePort=sourcePort==0?port:sourcePort; int udpLength=8+payload.Length;
        byte[] udp=[(byte)(sourcePort>>8),(byte)sourcePort,(byte)(port>>8),(byte)port,(byte)(udpLength>>8),(byte)udpLength,0,0];
        udp.CopyTo(p,20); payload.CopyTo(p,28);
        return p;
    }
    public static byte[] Tms(string text)
    {
        byte[] body=[0xA0,0x00,0x82,0x04,..Encoding.Unicode.GetBytes("\r\n"+text)];
        return [(byte)(body.Length>>8),(byte)body.Length,..body];
    }
    public static byte[] HyteraMessage(string text,int source=2001,int destination=1)
    {
        var t=Encoding.Unicode.GetBytes(text); int length=12+t.Length;
        byte[] p=[0x09,0x00,0xA1,(byte)(length>>8),(byte)length,0,0,0,1,10,(byte)(destination>>16),(byte)(destination>>8),(byte)destination,
            10,(byte)(source>>16),(byte)(source>>8),(byte)source,..t,0,0x03];
        int sum=0; for(int i=1;i<p.Length-2;i++) sum+=p[i];
        p[^2]=(byte)(~sum+0x33);
        return p;
    }
    private static List<DmrEvent> Feed(IEnumerable<byte[]> bursts,ProtocolDecoder? decoder=null,List<DmrEvent>? events=null)
    {
        events??=new(); decoder??=new(48000,events.Add); long sample=1000;
        foreach(var b in bursts) { decoder.Process(b,sample,0,1,"bs_data"); sample+=2880; }
        return events;
    }
    private static DmrEvent Single(List<DmrEvent> events,string type)
    {
        var list=events.Where(e=>e.Type==type).ToArray();
        Check(list.Length==1,$"Expected one {type}, got {list.Length}: {string.Join(" | ",list.Select(e=>e.ToJson()))}");
        return list[0];
    }

    public static void Register(Action<string,Action> test)
    {
        test("Talker alias and GPS LCs keep the call identity (alternating with the voice LC)",()=>
        {
            var c=new Call(); var voice=Synthetic.Lc(); var ta=TalkerAlias("UR5ABC",0);
            c.Header(voice);
            foreach(var lc in new[]{voice,ta[0],voice,ta[1],Gps(50.45,30.52,2),voice,ta[0],voice}) c.Superframe(lc);
            c.Terminator(voice);
            Check(c.Of("call_start").Length==1 && c.Of("call_end").Length==1,"Metadata LC split the call");
            Check(Equals(c.Of("call_end")[0].Data["reason"],"terminator"));
            Check(c.Of("vocoder_frame").All(e=>Equals(e.Data["source_id"],123456) && Equals(e.Data["destination_id"],91)),"Identity lost");
            Check(c.Of("vocoder_frame").All(e=>(string)e.Data["privacy_status"]! =="not_indicated"),"Metadata LC marked as vendor");
            Check(!c.Of("call_update").Any(e=>e.Data.ContainsKey("interpretation")),"Metadata LC treated as opaque");
            var alias=c.Of("talker_alias");
            Check(alias.Length==1 && (string)alias[0].Data["alias"]! =="UR5ABC" && Equals(alias[0].Data["complete"],true),"Alias not decoded once");
            Check(Equals(alias[0].Data["source_id"],123456) && alias[0].SessionId==c.Of("call_start")[0].SessionId);
            Check(c.Of("location").Length==1);
        });
        test("Talker alias full LC in a voice LC header slot does not start a new call",()=>
        {
            var c=new Call(); var voice=Synthetic.Lc(); var ta=TalkerAlias("DL1ABC",1);
            c.Header(voice); c.Superframe(voice); c.Header(ta[0]); c.Superframe(voice); c.Terminator(voice);
            Check(c.Of("call_start").Length==1 && c.Of("call_end").Length==1);
            Check(c.Of("vocoder_frame").All(e=>Equals(e.Data["source_id"],123456)));
            Check((string)c.Of("talker_alias").Single().Data["alias"]! =="DL1ABC");
        });
        test("Talker alias in every format, split across superframes, with growth events",()=>
        {
            var cases=new (string Text,int Format,string Name)[]
            {
                ("UR5ABC Mykola",0,"7bit"),("ABCDEFGHIJKLMNOPQRSTUVWXYZ01234",0,"7bit"),("Café Øst ÄÖ",1,"iso8"),
                ("ABCDEFGHIJKLMNOPQRSTUVWXYZ0",1,"iso8"),("Микола UR5",2,"utf8"),("Тарас UR5ABC",3,"utf16be"),("€ 😀 ok",3,"utf16be")
            };
            foreach(var (text,format,name) in cases)
            {
                var c=new Call(); var voice=Synthetic.Lc(); var ta=TalkerAlias(text,format);
                c.Header(voice);
                foreach(var lc in ta) { c.Superframe(voice); c.Superframe(lc); }
                c.Terminator(voice);
                var events=c.Of("talker_alias");
                Check(events.Length>=1,$"{name}: no alias");
                var last=events[^1];
                Check((string)last.Data["alias"]! ==text,$"{name}: '{last.Data["alias"]}' != '{text}'");
                Check(Equals(last.Data["complete"],true) && (string)last.Data["format"]! ==name,$"{name}: not complete");
                Check(events.Zip(events.Skip(1),(a,b)=>((string)b.Data["alias"]!).Length>((string)a.Data["alias"]!).Length || (Equals(b.Data["complete"],true) && !Equals(a.Data["complete"],true))).All(x=>x),$"{name}: event without growth");
                Check(events.Select(e=>(string)e.Data["alias"]!).All(a=>text.StartsWith(a,StringComparison.Ordinal)),$"{name}: partial alias is not a prefix");
                Check(events.Count(e=>Equals(e.Data["complete"],true))==1,$"{name}: complete emitted more than once");
            }
        });
        test("Talker alias with a lost block completes when the block is repeated; resets at call end",()=>
        {
            var c=new Call(); var voice=Synthetic.Lc(); string text="UR5ABC/P Kyiv 433"; var ta=TalkerAlias(text,1);
            c.Header(voice);
            c.Superframe(ta[0]); c.Superframe(ta[1]); c.Superframe(null); c.Superframe(ta[3]);
            var partial=c.Of("talker_alias");
            Check(partial.Length==2 && (string)partial[^1].Data["alias"]! ==text[..13] && Equals(partial[^1].Data["complete"],false),"Partial alias wrong");
            Check(((int[])partial[^1].Data["blocks_received"]!).SequenceEqual([0,1]));
            c.Superframe(voice); c.Superframe(ta[0]); c.Superframe(ta[1]); c.Superframe(ta[2]);
            var done=c.Of("talker_alias")[^1];
            Check((string)done.Data["alias"]! ==text && Equals(done.Data["complete"],true),"Alias did not complete");
            Check(((int[])done.Data["blocks_received"]!).SequenceEqual([0,1,2,3]));
            Check(c.Of("talker_alias").Length==3,"Repeated header or block re-emitted");
            c.Terminator(voice); int before=c.Of("talker_alias").Length;
            c.Header(voice); c.Superframe(ta[0]);
            Check(c.Of("talker_alias").Length==before+1,"Alias state survived the call end");
        });
        test("Talker alias resets on identity change",()=>
        {
            var c=new Call(); var ta=TalkerAlias("FIRST",0); var other=TalkerAlias("FIRST",0);
            c.Header(Synthetic.Lc()); c.Superframe(ta[0]);
            c.Superframe(Synthetic.Lc(777,91)); c.Superframe(other[0]);
            var events=c.Of("talker_alias");
            Check(events.Length==2 && Equals(events[1].Data["source_id"],777) && events[0].SessionId!=events[1].SessionId);
        });
        test("GPS info LC: position error, latitude, longitude (both hemispheres)",()=>
        {
            foreach(var (lat,lon,err) in new[]{(50.4501,30.5234,2),(-33.8688,-70.6693,7),(0.0,179.99,0),(-89.9,-179.99,5)})
            {
                var c=new Call(); c.Header(Synthetic.Lc()); c.Superframe(Gps(lat,lon,err)); c.Superframe(Gps(lat,lon,err));
                var location=Single(c.Events,"location");
                Check(Math.Abs((double)location.Data["latitude"]!-lat)<2e-5 && Math.Abs((double)location.Data["longitude"]!-lon)<2e-5,$"GPS {location.ToJson()}");
                Check(Equals(location.Data["position_error"],err) && Equals(location.Data["source"],"lc_gps") && Equals(location.Data["source_id"],123456));
            }
        });
        test("Rate 3/4 trellis: every state transition, adjacent-level symbol errors",()=>
        {
            var rng=new Random(5);
            byte[] Random144(int first,int second)
            {
                var d=Enumerable.Range(0,144).Select(_=>(byte)rng.Next(2)).ToArray();
                for(int k=0;k<3;k++) { d[k]=(byte)((first>>(2-k))&1); d[3+k]=(byte)((second>>(2-k))&1); }
                return d;
            }
            for(int s=0;s<8;s++) for(int j=0;j<8;j++)
            {
                var data=Random144(s,j); var r=Trellis.Decode34(Trellis.Encode34(data));
                Check(r.Data.SequenceEqual(data) && r.Corrections==0,$"Transition {s}->{j}");
            }
            // Spec tables B.7, B.8, B.10 transcribed independently: all-zero data is point 0 (+1,-1) = dibits 00,10 everywhere;
            // tribits 7,7 give points 14 (+3,+1) then 10 (-1,+1): encoder dibits 0..3 = 01,00,10,00 at TX dibits 0,1,26,27.
            var zero=Trellis.Encode34(new byte[144]);
            for(int i=0;i<98;i++) Check(zero[2*i]==(i&1) && zero[2*i+1]==0,$"All-zero dibit {i}");
            var sevens=new byte[144]; for(int k=0;k<6;k++) sevens[k]=1;
            var air=Trellis.Encode34(sevens);
            Check(air[0]==0 && air[1]==1 && air[2]==0 && air[3]==0 && air[52]==1 && air[53]==0 && air[54]==0 && air[55]==0,"Table B.7/B.8 points 14 and 10");
            // Symbol errors: every error to an adjacent level (+3/+1/-1/-3) at every position must be corrected, and three at once
            // when they are at least eight trellis steps apart. Jumps of two or three levels are beyond this hard-decision code
            // (they need noise above 1.5 level spacings); their correction rate is reported, not asserted.
            static int Level(byte[] a,int i)=>new[]{1,3,-1,-3}[(a[2*i]<<1)|a[2*i+1]];
            static void SetLevel(byte[] a,int i,int level) { int d=level switch {1=>0,3=>1,-1=>2,_=>3}; a[2*i]=(byte)(d>>1); a[2*i+1]=(byte)(d&1); }
            var txOfEncoder=new int[98]; { int t=0; for(int off=0;off<8;off+=2) for(int e=off;e+1<98;e+=8) { txOfEncoder[e]=t++; txOfEncoder[e+1]=t++; } }
            int largeFailures=0, largeTrials=0;
            for(int trial=0;trial<8;trial++)
            {
                var data=Random144(rng.Next(8),rng.Next(8)); var clean=Trellis.Encode34(data);
                for(int i=0;i<98;i++) foreach(int target in new[]{1,3,-1,-3})
                {
                    int steps=Math.Abs(target-Level(clean,i))/2; if(steps==0) continue;
                    var bad=(byte[])clean.Clone(); SetLevel(bad,i,target); var r=Trellis.Decode34(bad);
                    if(steps==1) Check(r.Data.SequenceEqual(data) && r.Corrections==1,$"Adjacent-level error at dibit {i}");
                    else { largeTrials++; if(!r.Data.SequenceEqual(data)) largeFailures++; }
                }
                for(int first=0;first<49-2*8;first++)
                {
                    var three=(byte[])clean.Clone();
                    foreach(int step in new[]{first,first+8,first+16})
                    {
                        int tx=txOfEncoder[Math.Min(97,2*step+((trial+step)&1))], level=Level(three,tx);
                        SetLevel(three,tx,level==3?1:level==-3?-1:((step&1)==0?level+2:level-2));
                    }
                    var r=Trellis.Decode34(three);
                    Check(r.Data.SequenceEqual(data) && r.Corrections==3,$"Three adjacent-level errors from step {first}");
                }
            }
            Console.WriteLine($"     two- and three-level symbol jumps corrected: {largeTrials-largeFailures}/{largeTrials}");
        });
        test("Rate 1 layout and CRC-32/CRC-9 helpers against the generic bit CRC",()=>
        {
            var rng=new Random(9); var data=Enumerable.Range(0,192).Select(_=>(byte)rng.Next(2)).ToArray();
            var air=Trellis.EncodeRate1(data); Check(air[96..100].All(x=>x==0) && Trellis.DecodeRate1(air).SequenceEqual(data));
            for(int n=2;n<40;n+=2)
            {
                var octets=new byte[n]; rng.NextBytes(octets);
                var swapped=Enumerable.Range(0,n).Select(i=>octets[i^1]).ToArray();
                Check(Checks.DataCrc32(octets)==Checks.Crc(Bits.Unpack(swapped),32,0x04C11DB7),"CRC-32 word order");
                int serial=rng.Next(128);
                Check(Checks.Crc9(octets,serial)==(int)(Checks.Crc(Bits.Unpack(octets).Concat(Bits.FromUInt((uint)serial,7)).ToArray(),9,0x059)^0x1FF),"CRC-9");
            }
        });
        test("Unconfirmed and confirmed packets at rate 1/2, 3/4 and 1: good and bad CRC",()=>
        {
            var rng=new Random(3);
            foreach(int type in new[]{7,8,10}) foreach(bool confirmed in new[]{false,true}) foreach(bool corrupt in new[]{false,true})
            {
                var user=new byte[37+type]; rng.NextBytes(user);
                var events=Feed(Packet(user,type,confirmed,sap:5,corrupt:corrupt));
                var packet=Single(events,"data_packet");
                string label=$"type {type} confirmed {confirmed} corrupt {corrupt}";
                Check(Equals(packet.Data["crc_valid"],!corrupt),$"{label}: crc_valid {packet.ToJson()}");
                Check(Equals(packet.Data["complete"],true) && Equals(packet.Data["confirmed"],confirmed) && Equals(packet.Data["rate"],type==7?"half":type==8?"three_quarters":"one"),label);
                if(!corrupt) Check((string)packet.Data["bytes_hex"]! ==Convert.ToHexString(user),$"{label}: bytes");
                if(confirmed) Check(Equals(packet.Data["crc9_failures"],corrupt?1:0),$"{label}: CRC-9 {packet.Data["crc9_failures"]}");
                Check(packet.SessionId==null && !events.Any(e=>e.Type=="call_start"),$"{label}: data invented a call");
                Check(Equals(packet.Data["source_id"],2001) && Equals(packet.Data["destination_id"],1) && Equals(packet.Data["sap"],5));
            }
        });
        test("Confirmed blocks are ordered by serial number; incomplete packet is flushed by voice",()=>
        {
            var user=Enumerable.Range(0,60).Select(i=>(byte)i).ToArray();
            var bursts=Packet(user,8,true,sap:5);
            List<byte[]> reordered=[bursts[0],bursts[2],bursts[1],..bursts.Skip(3)];
            var packet=Single(Feed(reordered),"data_packet");
            Check(Equals(packet.Data["crc_valid"],true) && (string)packet.Data["bytes_hex"]! ==Convert.ToHexString(user),"Serial ordering");
            var events=new List<DmrEvent>(); var p=new ProtocolDecoder(48000,events.Add);
            Feed(bursts.Take(2),p,events);
            p.Process(Synthetic.Voice(Synthetic.Lc(),0,7,Socket),900000,0,1,"bs_voice");
            var partial=Single(events,"data_packet");
            Check(Equals(partial.Data["complete"],false) && Equals(partial.Data["crc_valid"],false) && Equals(partial.Data["blocks"],1));
            Check((string)partial.Data["bytes_hex"]! ==Convert.ToHexString(user[..16]),"Incomplete packet must keep every received octet");
        });
        test("ETSI defined short data text: 7-bit, ISO 8859-1/-5, UTF-8, UTF-16BE/LE",()=>
        {
            var cases=new (int Dd,string Text,byte[] Bytes,int Bits)[]
            {
                (2,"Hello DMR 7bit",Pack7("Hello DMR 7bit"),7*14),
                (3,"Grüße",Encoding.Latin1.GetBytes("Grüße"),40),
                (7,"Привет",new byte[]{0xBF,0xE0,0xD8,0xD2,0xD5,0xE2},48),
                (18,"Слава Україні",Encoding.UTF8.GetBytes("Слава Україні"),8*Encoding.UTF8.GetByteCount("Слава Україні")),
                (20,"UTF16 BE ✓",Encoding.BigEndianUnicode.GetBytes("UTF16 BE ✓"),8*20),
                (21,"UTF16 LE",Encoding.Unicode.GetBytes("UTF16 LE"),8*16),
            };
            foreach(var (dd,text,bytes,bits) in cases)
            {
                var events=Feed(DefinedShortData(bytes,bits,dd));
                var packet=Single(events,"data_packet"); var message=Single(events,"text_message");
                Check(Equals(packet.Data["crc_valid"],true) && Equals(packet.Data["application"],"etsi_short_data"),$"DD {dd}: {packet.ToJson()}");
                Check((string)message.Data["text"]! ==text && Equals(message.Data["protocol"],"etsi_short_data"),$"DD {dd}: '{message.Data["text"]}'");
                Check(Equals(message.Data["source_id"],2001) && Equals(message.Data["crc_valid"],true) && message.SessionId==null);
            }
            Check(Single(Feed(DefinedShortData(new byte[]{1,2,3},24,0)),"data_packet").Data["bytes_hex"] is "010203","Binary short data");
        });
        test("Motorola TMS over IPv4/UDP 4007 at rate 1/2 and 3/4",()=>
        {
            foreach(int type in new[]{7,8})
            {
                string text="Привіт from TMS #"+type;
                var events=Feed(Packet(Udp(Tms(text),4007),type,false,sap:4,source:2001,destination:2002));
                var packet=Single(events,"data_packet"); var message=Single(events,"text_message");
                Check(Equals(packet.Data["udp_destination_port"],4007) && Equals(packet.Data["ip_checksum_valid"],true) && Equals(packet.Data["application"],"motorola_tms"));
                Check((string)message.Data["text"]! ==text && Equals(message.Data["protocol"],"motorola_tms") && Equals(message.Data["confidence"],"high"),$"TMS '{message.Data["text"]}'");
                Check(Equals(message.Data["port"],4007) && Equals(message.Data["encoding"],"utf16le") && Equals(message.Data["destination_id"],2002));
            }
            // An acknowledgement carries no text.
            var ack=Feed(Packet(Udp([0x00,0x03,0xBF,0x00,0x82],4007),7,false));
            Check(!ack.Any(e=>e.Type=="text_message") && ack.Count(e=>e.Type=="data_packet")==1);
        });
        test("Hytera text messaging over UDP 5016 and 3009; unknown ports only when clearly text",()=>
        {
            foreach(int port in new[]{5016,3009})
            {
                var events=Feed(Packet(Udp(HyteraMessage("Hytera тест 5016"),port),7,false));
                var message=Single(events,"text_message");
                Check((string)message.Data["text"]! =="Hytera тест 5016" && Equals(message.Data["protocol"],"hytera") && Equals(message.Data["confidence"],"high"),$"Hytera {message.ToJson()}");
            }
            var unknown=Single(Feed(Packet(Udp(Encoding.Unicode.GetBytes("plain unicode text"),6000),7,false)),"text_message");
            Check(Equals(unknown.Data["protocol"],"ip_udp_text") && Equals(unknown.Data["confidence"],"low") && (string)unknown.Data["text"]! =="plain unicode text");
            var ascii=Single(Feed(Packet(Udp(Encoding.ASCII.GetBytes("HELLO ascii over udp"),6001),7,false)),"text_message");
            Check((string)ascii.Data["text"]! =="HELLO ascii over udp" && Equals(ascii.Data["encoding"],"ascii"));
            var rng=new Random(1); var binary=new byte[40]; rng.NextBytes(binary);
            var events2=Feed(Packet(Udp(binary,6002),7,false));
            Check(!events2.Any(e=>e.Type=="text_message") && events2.Count(e=>e.Type=="data_packet")==1,"Binary became text");
        });
        test("Data header fields, status short data, proprietary second header",()=>
        {
            var events=Feed(Packet(new byte[20],7,true,sap:4));
            var header=Single(events,"data_header");
            Check(Equals(header.Data["dpf_name"],"confirmed") && Equals(header.Data["blocks_to_follow"],3) && Equals(header.Data["pad_octet_count"],6) && Equals(header.Data["fsn"],8));
            // Status/precoded short data: DPF 1110 with no appended blocks; SP 1, DP 2, status 0x123.
            byte[] status=[14,0xA0,0,0,1,0,7,209,0b001_010_01,0x23];
            var e2=Feed([HeaderBurst(status)]);
            var h2=Single(e2,"data_header");
            Check(Equals(h2.Data["short_data_kind"],"status_precoded") && Equals(h2.Data["status_precoded"],0x123) && !e2.Any(e=>e.Type=="data_packet"));
            // Proprietary: first header SAP 9 announces a second header (DPF 1111, MFID), which is not user data.
            var user=new byte[8]; var bursts=Packet(user,7,false,sap:9);
            var first=Bits.Pack(Burst.DecodeData(bursts[0]).Payload)[..10]; first[8]=(byte)(0x80|((first[8]&127)+1));
            List<byte[]> list=[HeaderBurst(first),HeaderBurst([0x9F,0x10,1,2,3,4,5,6,7,8]),..bursts.Skip(1)];
            var e3=Feed(list); var packet=Single(e3,"data_packet");
            Check(Equals(packet.Data["mfid"],0x10) && Equals(packet.Data["crc_valid"],true) && (string)packet.Data["bytes_hex"]! =="0000000000000000",$"Proprietary {packet.ToJson()}");
        });
    }

    private static byte[] Pack7(string text)
    {
        var bits=text.SelectMany(c=>Bits.FromUInt(c,7)).ToList();
        while(bits.Count%8!=0) bits.Add(0);
        return Bits.Pack(bits.ToArray());
    }
}

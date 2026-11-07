using Dmr.Fec;

namespace Dmr.Protocol;

/// <summary>Data header parsing, block decoding, packet reassembly and application decoding (TS 102 361-1 clause 8.2,
/// 9.3 and Annex B; TS 102 361-3 short data and IP bearer; vendor text messaging over UDP).</summary>
public sealed partial class ProtocolDecoder
{
    private sealed class PacketAssembly
    {
        public bool Active;
        public long LastSample;
        public readonly byte[] Header = new byte[10];
        public byte[]? SecondHeader;
        public int Dpf, Sap, Source, Destination, Expected, PendingHeaders, Count, BlockType = -1;
        public bool Group, Confirmed, ShortData;
        public int PadOctets, PadBits, PadNibbles;
        public int? DdFormat, UdtFormat, Mfid;
        public int FecFailures, Corrections;
        /// <summary>Raw decoded blocks in arrival order (up to 127 blocks of 24 octets).</summary>
        public readonly byte[] Raw = new byte[127 * 24];
        public void Clear()
        {
            Active = false; LastSample = 0; Array.Clear(Header); SecondHeader = null;
            Dpf = Sap = Source = Destination = Expected = PendingHeaders = Count = 0; BlockType = -1;
            Group = Confirmed = ShortData = false; PadOctets = PadBits = PadNibbles = 0;
            DdFormat = UdtFormat = Mfid = null; FecFailures = Corrections = 0;
        }
    }

    public static string DpfName(int dpf) => dpf switch
    {
        0 => "udt", 1 => "response", 2 => "unconfirmed", 3 => "confirmed", 13 => "defined_short_data",
        14 => "raw_short_data", 15 => "proprietary", _ => "reserved"
    };
    private static int BlockOctets(int type, bool confirmed) => type switch { 7 => 12, 8 => 18, _ => 24 } - (confirmed ? 2 : 0);
    private static string RateName(int type) => type switch { 7 => "half", 8 => "three_quarters", 10 => "one", _ => "unknown" };
    private static int Crc9Mask(int type) => type switch { 7 => 0x0F0, 8 => 0x1FF, _ => 0x10F };

    private void ParseDataHeader(byte[] bits,int track,long sample)
    {
        var b=Bits.Pack(bits); int dpf=b[0]&15;
        var data=new Dictionary<string,object?> { ["dpf"]=dpf,["raw_hex"]=Convert.ToHexString(b),["provenance"]="data_header" };
        if(dpf is 0 or 1 or 2 or 3 or 13 or 14)
        {
            data["group"]=(b[0]&0x80)!=0; data["sap"]=(b[1]>>4)&15;
            data["destination_id"]=(b[2]<<16)|(b[3]<<8)|b[4]; data["source_id"]=(b[5]<<16)|(b[6]<<8)|b[7];
            data["response_requested"]=(b[0]&0x40)!=0;
        }
        data["dpf_name"]=DpfName(dpf);
        var s=tracks[track]; var a=s.Packet;
        // A proprietary second header (DPF 1111) continues a packet whose first header announced SAP 9.
        if(dpf==15 && a.Active && a.PendingHeaders>0 && a.Count==0)
        {
            a.PendingHeaders--; a.SecondHeader=b[..10]; a.Mfid=b[1]; a.LastSample=sample;
            data["sap"]=b[0]>>4; data["mfid"]=b[1]; data["second_header"]=true;
            Emit("data_header",sample,track,data);
            return;
        }
        if(a.Active) FlushPacket(track,sample);
        int expected=0, pendingHeaders=0; bool confirmed=false, shortData=false;
        int sap=(b[1]>>4)&15;
        switch(dpf)
        {
            case 2: case 3:
            {
                int bf=b[8]&127, poc=(((b[0]>>4)&1)<<4)|(b[1]&15);
                data["header_compression"]=(b[0]&0x20)!=0; data["pad_octet_count"]=poc;
                data["full_message"]=(b[8]&0x80)!=0; data["blocks_to_follow"]=bf; data["fsn"]=b[9]&15;
                if(dpf==3) { data["resync"]=(b[9]&0x80)!=0; data["send_sequence"]=(b[9]>>4)&7; }
                confirmed=dpf==3; pendingHeaders=sap==9?1:0; expected=bf-pendingHeaders; a.PadOctets=poc;
                break;
            }
            case 1:
                data["blocks_to_follow"]=b[8]&127; data["response_class"]=b[9]>>6; data["response_type"]=(b[9]>>3)&7; data["response_status"]=b[9]&7;
                expected=b[8]&127;
                break;
            case 13: case 14:
            {
                int ab=(((b[0]>>4)&3)<<4)|(b[1]&15);
                data["appended_blocks"]=ab;
                if(dpf==13)
                {
                    a.DdFormat=b[8]>>2; data["dd_format"]=a.DdFormat; data["dd_format_name"]=DataApplications.DefinedFormatName(a.DdFormat.Value);
                    data["sarq"]=(b[8]&2)!=0; data["full_message"]=(b[8]&1)!=0; data["bit_padding"]=b[9];
                }
                else if(ab==0)
                {
                    data["short_data_kind"]="status_precoded"; data["source_port"]=b[8]>>5; data["destination_port"]=(b[8]>>2)&7;
                    data["status_precoded"]=((b[8]&3)<<8)|b[9];
                }
                else
                {
                    data["short_data_kind"]="raw"; data["source_port"]=b[8]>>5; data["destination_port"]=(b[8]>>2)&7;
                    data["sarq"]=(b[8]&2)!=0; data["full_message"]=(b[8]&1)!=0; data["bit_padding"]=b[9];
                }
                expected=ab; shortData=true; confirmed=(b[8]&2)!=0 && !(dpf==14 && ab==0); a.PadBits=b[9];
                break;
            }
            case 0:
                a.UdtFormat=b[1]&15; data["udt_format"]=a.UdtFormat; data["pad_nibbles"]=b[8]>>3; data["udt_appended_blocks"]=(b[8]&3)+1;
                data["supplementary_flag"]=(b[9]&0x80)!=0; data["protect_flag"]=(b[9]&0x40)!=0; data["udt_opcode"]=b[9]&63;
                expected=(b[8]&3)+1; a.PadNibbles=b[8]>>3;
                break;
            case 15:
                data["sap"]=b[0]>>4; data["mfid"]=b[1];
                break;
        }
        Emit("data_header",sample,track,data);
        if(expected<=0 && pendingHeaders==0) { a.Clear(); return; }
        b.AsSpan(0,10).CopyTo(a.Header);
        a.Active=true; a.LastSample=sample; a.Dpf=dpf; a.Sap=sap; a.Group=(b[0]&0x80)!=0;
        a.Destination=(b[2]<<16)|(b[3]<<8)|b[4]; a.Source=(b[5]<<16)|(b[6]<<8)|b[7];
        a.Expected=Math.Max(expected,0); a.PendingHeaders=pendingHeaders; a.Confirmed=confirmed; a.ShortData=shortData;
        a.Count=0; a.BlockType=-1; a.FecFailures=0; a.Corrections=0;
    }

    private void DataBlock(DataBurst d,int track,long sample)
    {
        var s=tracks[track]; var a=s.Packet;
        if(!a.Active) return;
        if(sample-a.LastSample>sampleRate) { FlushPacket(track,sample); return; }
        byte[] octets; int corrections; bool fecOk=true;
        switch(d.Type)
        {
            case 7: octets=Bits.Pack(d.Payload); corrections=d.FecCorrections; fecOk=d.FecValid; break;
            case 8: { var r=Trellis.Decode34(d.Payload); octets=Bits.Pack(r.Data); corrections=r.Corrections; break; }
            default: octets=Bits.Pack(Trellis.DecodeRate1(d.Payload)); corrections=0; break;
        }
        a.LastSample=sample;
        if(a.PendingHeaders>0)
        {
            // A second header sent as a data block: keep it, it is not user data.
            a.PendingHeaders--; a.SecondHeader=octets[..Math.Min(10,octets.Length)]; a.Mfid=octets[1];
            return;
        }
        if(a.BlockType<0) a.BlockType=d.Type;
        else if(a.BlockType!=d.Type) { FlushPacket(track,sample); return; }
        if(a.Dpf==0 && d.Type!=7) { FlushPacket(track,sample); return; }
        int length=octets.Length;
        if((a.Count+1)*length>a.Raw.Length) { FlushPacket(track,sample); return; }
        octets.CopyTo(a.Raw,a.Count*length); a.Count++;
        if(!fecOk) a.FecFailures++;
        a.Corrections+=corrections;
        if(a.Count>=a.Expected) FinishPacket(track,sample,true);
    }

    /// <summary>Emits what an unfinished packet holds (complete = false), then forgets it.</summary>
    private void FlushPacket(int track,long sample)
    {
        var a=tracks[track].Packet;
        if(a.Active && a.Count>0) FinishPacket(track,sample,false);
        a.Clear();
    }

    private void FinishPacket(int track,long sample,bool complete)
    {
        var a=tracks[track].Packet;
        int type=a.BlockType, raw=type switch { 7=>12, 8=>18, _=>24 };
        bool confirmed=a.Confirmed;
        var user=Assemble(a,type,raw,confirmed,complete,out bool crcValid,out int crc9Failures,out int bits,out string? crcNote);
        // Short data: the SARQ bit decides the block layout; if the message CRC disagrees, try the other layout.
        if(a.ShortData && complete && !crcValid)
        {
            var other=Assemble(a,type,raw,!confirmed,true,out bool otherValid,out int otherCrc9,out int otherBits,out string? otherNote);
            if(otherValid) { user=other; crcValid=true; crc9Failures=otherCrc9; bits=otherBits; crcNote=otherNote; confirmed=!confirmed; }
        }
        if(!complete) crcValid=false;
        var data=new Dictionary<string,object?> {
            ["source_id"]=a.Dpf==15?null:a.Source,["destination_id"]=a.Dpf==15?null:a.Destination,["group"]=a.Group,
            ["sap"]=a.Sap,["dpf"]=a.Dpf,["dpf_name"]=DpfName(a.Dpf),["confirmed"]=confirmed,["rate"]=RateName(type),
            ["blocks"]=a.Count,["blocks_expected"]=a.Expected,["complete"]=complete,["crc_valid"]=crcValid,
            ["bytes_hex"]=Convert.ToHexString(user),["header_hex"]=Convert.ToHexString(a.Header),
            ["blocks_hex"]=Convert.ToHexString(a.Raw.AsSpan(0,a.Count*raw)),
            ["fec_failures"]=a.FecFailures,["corrected_bits"]=a.Corrections };
        if(confirmed) data["crc9_failures"]=crc9Failures;
        if(crcNote!=null) data["crc_note"]=crcNote;
        if(a.Dpf is 2 or 3) data["pad_octet_count"]=a.PadOctets;
        if(a.ShortData) { data["bit_padding"]=a.PadBits; data["user_bits"]=bits; }
        if(a.DdFormat.HasValue) data["dd_format"]=a.DdFormat;
        if(a.UdtFormat.HasValue) { data["udt_format"]=a.UdtFormat; data["pad_nibbles"]=a.PadNibbles; data["user_bits"]=bits; }
        if(a.SecondHeader!=null) { data["second_header_hex"]=Convert.ToHexString(a.SecondHeader); data["mfid"]=a.Mfid; }
        TextDecode? text=null; string? application=null; int? port=null;
        if(complete && a.Dpf==13 && a.DdFormat.HasValue)
        {
            application="etsi_short_data";
            if(DataApplications.DefinedShortData(a.DdFormat.Value,user,bits) is var (t,enc))
                text=new(t,enc,"etsi_short_data","high","");
        }
        else if(complete && a.Dpf==0 && a.UdtFormat.HasValue)
        {
            application="etsi_udt";
            if(DataApplications.UnifiedDataTransport(a.UdtFormat.Value,user,bits) is var (t,enc))
                text=new(t,enc,"etsi_udt","high","");
        }
        else if(complete && a.Sap==4 && DataApplications.TryParseUdp(user,out var udp))
        {
            data["ip_source"]=udp.SourceIp; data["ip_destination"]=udp.DestinationIp; data["ip_checksum_valid"]=udp.IpChecksumValid;
            data["udp_source_port"]=udp.SourcePort; data["udp_destination_port"]=udp.DestinationPort;
            data["udp_payload_hex"]=Convert.ToHexString(user.AsSpan(udp.PayloadOffset,udp.PayloadLength));
            port=udp.DestinationPort;
            application=udp.DestinationPort switch { DataApplications.MotorolaTmsPort=>"motorola_tms",DataApplications.MotorolaLrrpPort=>"motorola_lrrp",
                DataApplications.HyteraTextPort or DataApplications.HyteraDataPort=>"hytera",_=>"udp" };
            if(!udp.Fragment) text=DataApplications.UdpText(udp,user.AsSpan(udp.PayloadOffset,udp.PayloadLength));
        }
        else if(complete && a.Sap is 2 or 3 && user.Length>0)
        {
            // Compressed UDP/IP header (TS 102 361-3 clause 5.6): not parsed; only clearly textual payloads give a message.
            application="udp_compressed";
            text=DataApplications.Scan(user,"ip_udp_text",strict:true);
        }
        data["application"]=application;
        EmitData("data_packet",sample,track,data);
        if(text!=null && (text.Text.Length>0))
        {
            var message=new Dictionary<string,object?> {
                ["source_id"]=a.Source,["destination_id"]=a.Destination,["group"]=a.Group,["text"]=text.Text,["encoding"]=text.Encoding,
                ["protocol"]=text.Protocol,["port"]=port,["crc_valid"]=crcValid,["confidence"]=text.Confidence };
            if(text.HeaderHex.Length>0) message["header_hex"]=text.HeaderHex;
            if(data.TryGetValue("ip_source",out var ipSource)) { message["ip_source"]=ipSource; message["ip_destination"]=data["ip_destination"]; }
            EmitData("text_message",sample,track,message);
        }
        a.Clear();
    }

    /// <summary>User data of a complete packet without pad and message CRC; for an incomplete packet, every octet received.</summary>
    private static byte[] Assemble(PacketAssembly a,int type,int raw,bool confirmed,bool complete,out bool crcValid,out int crc9Failures,out int bits,out string? note)
    {
        crc9Failures=0; note=null;
        int per=raw-(confirmed?2:0), count=a.Count;
        var payload=new byte[count*per];
        if(confirmed)
        {
            // Order by serial number when every block's CRC-9 holds and the serials are a permutation; else arrival order.
            Span<int> order=stackalloc int[count]; bool ordered=true; ulong seen0=0, seen1=0;
            for(int i=0;i<count;i++)
            {
                var block=a.Raw.AsSpan(i*raw,raw); int serial=block[0]>>1, crc=((block[0]&1)<<8)|block[1];
                bool ok=(Checks.Crc9(block[2..],serial)^Crc9Mask(type))==crc;
                if(!ok) crc9Failures++;
                order[i]=serial;
                ref ulong seen=ref serial<64?ref seen0:ref seen1; ulong bit=1UL<<(serial&63);
                if(!ok || serial>=count || (seen&bit)!=0) ordered=false; seen|=bit;
            }
            for(int i=0;i<count;i++) a.Raw.AsSpan(i*raw+2,per).CopyTo(payload.AsSpan((ordered?order[i]:i)*per));
        }
        else for(int i=0;i<count;i++) a.Raw.AsSpan(i*raw,per).CopyTo(payload.AsSpan(i*per));
        if(!complete) { crcValid=false; bits=payload.Length*8; return payload; }
        if(a.Dpf==0)
        {
            // UDT: CRC-CCITT over the appended blocks in the last two octets, then pad nibbles before it.
            crcValid=false; bits=0;
            if(payload.Length<2) return payload;
            uint received=(uint)((payload[^2]<<8)|payload[^1]);
            uint computed=Checks.Crc(Bits.Unpack(payload.AsSpan(0,payload.Length-2)),16,0x1021,finalXor:0xffff);
            foreach(uint mask in new uint[] {0x3333,0x0000,0xCCCC,0xAAAA,0x6969})
                if((computed^mask)==received) { crcValid=true; note=$"udt_crc_mask_{mask:X4}"; break; }
            bits=Math.Max(0,(payload.Length-2)*8-4*a.PadNibbles);
            return payload[..((bits+7)/8)];
        }
        if(payload.Length<4) { crcValid=false; bits=0; return payload; }
        var body=payload.AsSpan(0,payload.Length-4); var crcField=payload.AsSpan(payload.Length-4);
        crcValid=Checks.DataCrc32(body)==Checks.ReadDataCrc32(crcField);
        if(!crcValid)
        {
            // Not in the specification: some implementations write the CRC most significant octet first. Reported, not trusted.
            uint value=(uint)((crcField[0]<<24)|(crcField[1]<<16)|(crcField[2]<<8)|crcField[3]);
            if(Checks.DataCrc32(body)==value) note="crc32_big_endian";
        }
        if(a.ShortData)
        {
            bits=Math.Max(0,body.Length*8-a.PadBits);
            return body[..((bits+7)/8)].ToArray();
        }
        int pad=Math.Clamp(a.Dpf is 2 or 3 ? a.PadOctets : 0,0,body.Length);
        bits=(body.Length-pad)*8;
        return body[..(body.Length-pad)].ToArray();
    }

    private void EmitData(string type,long sample,int track,Dictionary<string,object?> data)
    {
        var s=tracks[track];
        sink(new(1,sequence++,type,captureId,sample,sample/sampleRate,track,s.Slot,s.VoiceActive?s.Session:null,data));
    }
}

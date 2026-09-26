using Dmr.Fec;
using Dmr.Vocoder;
using Dmr.Privacy;
using System.Buffers.Binary;

namespace Dmr.Protocol;

/// <summary>Incremental burst-to-event decoder. One instance per selected RF carrier.
/// Calls are synchronous; the caller owns scheduling and must not call concurrently.</summary>
public sealed class ProtocolDecoder
{
    private sealed class TrackState
    {
        public string? Session;
        public int? Slot, Colour, Source, Destination;
        public string? CallType;
        public string Privacy = "unknown";
        public int Phase = -1, Revision;
        public long Frame, LastSample;
        public bool VoiceActive;
        public bool UnknownFeature;
        public bool StandardFeature;
        public DmraArc4Context? Cipher;
        public int? PrivacyFid, AlgorithmId, KeyId;
        public string? PrivacyProfile;
        public string DecryptionStatus = "missing_parameters";
        public readonly List<byte> Embedded = new();
        public Dictionary<string,object?> Metadata = new();
    }
    private readonly TrackState[] tracks = [new(),new()];
    private readonly List<byte> shortLc = new();
    private readonly Action<DmrEvent> sink;
    private readonly double sampleRate;
    private readonly string captureId;
    private readonly Arc4Keyring? privacyKeys;
    private long sequence, sessions, burstId;
    public ProtocolDecoder(double sampleRate, Action<DmrEvent> sink, string captureId = "capture", Arc4Keyring? privacyKeys = null)
    {
        this.sampleRate = sampleRate; this.sink = sink; this.captureId = captureId;
        this.privacyKeys = privacyKeys;
    }
    public void Emit(string type, long sample, int track, Dictionary<string,object?> data, int? slot = null)
    {
        var s = track >= 0 ? tracks[track] : null;
        sink(new(1, sequence++, type, captureId, sample, sample / sampleRate, track,
            slot ?? s?.Slot, s?.Session, data));
    }
    public bool VoiceActive(int track) => tracks[track].VoiceActive;
    public void Process(ReadOnlySpan<byte> bits, long sample, int track, int? slot, string kind, double syncScore = 0, double symbolError = 0)
    {
        if (track is <0 or >1 || bits.Length != 264) throw new ArgumentException("Invalid burst.");
        var s = tracks[track]; s.Slot = slot ?? s.Slot; s.LastSample = sample;
        long id = ++burstId;
        Emit("burst",sample,track,new() { ["burst_id"]=id,["kind"]=kind,["raw_hex"]=Bits.Hex(bits),["sync_score"]=syncScore,["symbol_error"]=symbolError });
        bool voiceSync = kind.EndsWith("_voice",StringComparison.Ordinal);
        bool dataSync = kind.EndsWith("_data",StringComparison.Ordinal);
        if (dataSync || !voiceSync)
        {
            var d = Burst.DecodeData(bits);
            if (dataSync || d.IntegrityValid)
            {
                Emit("pdu",sample,track,new() {
                    ["burst_id"]=id,["pdu_type"]=d.Type < 0 ? "unknown" : Burst.DataTypes[d.Type],
                    ["colour_code"]=d.ColourCode < 0 ? null : d.ColourCode,["raw_hex"]=Bits.Hex(d.Payload),
                    ["integrity_valid"]=d.IntegrityValid,["fec_valid"]=d.FecValid,
                    ["fec_corrected_bits"]=d.FecCorrections,["rs_corrected_bytes"]=d.RsCorrectedBytes });
                if (d.IntegrityValid)
                {
                    if (d.Type is 1 or 2)
                    {
                        if (d.Type == 1 && s.VoiceActive) End(track,sample,"new_header");
                        s.Colour = d.ColourCode;
                        if (d.Type == 1) Start(track,sample,"header");
                        ApplyLc(d.Payload[..72],track,sample,d.Type==1?"voice_lc_header":"terminator_lc");
                        if (d.Type == 2) End(track,sample,"terminator");
                    }
                    else if (d.Type == 0)
                    {
                        ParsePrivacyHeader(d.Payload,track,sample,d.ColourCode);
                    }
                    else if (d.Type == 3) ParseCsbk(d.Payload,track,sample);
                    else if (d.Type == 6) ParseDataHeader(d.Payload,track,sample);
                }
                else if (d.Type == 0 && s.Cipher != null)
                {
                    ClearPrivacyContext(s); s.DecryptionStatus="invalid_privacy_header";
                    Update(track,sample,new() { ["decryption_status"]=s.DecryptionStatus,["provenance"]="invalid_pi_header" });
                }
                if (s.VoiceActive && d.Type != 0) End(track,sample,d.IntegrityValid ? "data_transition" : "unvalidated_data_transition");
                s.Embedded.Clear(); s.Phase=-1;
                return;
            }
        }
        if (voiceSync)
        {
            Start(track,sample,"late_entry"); s.VoiceActive=true; s.Phase=0; s.Embedded.Clear();
        }
        else if (!s.VoiceActive) return;
        else s.Phase=(s.Phase+1)%6;
        if (!voiceSync)
        {
            var emb = Burst.Emb(bits);
            if (emb.Valid)
            {
                int colour=(int)(emb.Data>>3), pi=(int)((emb.Data>>2)&1), lcss=(int)(emb.Data&3);
                s.Colour=colour;
                string privacy=pi==1?"indicated":s.Privacy=="indicated"?"indicated":s.UnknownFeature?"unknown_vendor":"not_indicated";
                if (s.Privacy != privacy) { s.Privacy=privacy; Update(track,sample,new() { ["privacy_status"]=privacy,["raw_pi"]=pi,["provenance"]="emb" }); }
                Emit("pdu",sample,track,new() { ["pdu_type"]="emb",["colour_code"]=colour,["raw_pi"]=pi,["lcss"]=lcss,["corrected_bits"]=emb.Corrections });
                if (lcss==1) s.Embedded.Clear();
                if (lcss==1 || (lcss is 2 or 3 && s.Embedded.Count>0)) s.Embedded.AddRange(bits.Slice(116,32).ToArray());
                if (lcss==2)
                {
                    if (s.Embedded.Count==128)
                    {
                        var lc=ProductCodes.DecodeEmbedded(s.Embedded.ToArray());
                        Emit("pdu",sample,track,new() { ["pdu_type"]="embedded_lc",["raw_hex"]=Bits.Hex(lc.Data),["integrity_valid"]=lc.Valid,["corrected_bits"]=lc.Corrections });
                        if (lc.Valid) ApplyLc(lc.Data,track,sample,"embedded_lc");
                    }
                    s.Embedded.Clear();
                }
                if (s.Embedded.Count>128) s.Embedded.Clear();
            }
            else s.Embedded.Clear();
        }
        s.Cipher?.AlignBurst(s.Phase,voiceSync);
        var socket=Burst.VoiceSocket(bits);
        for (int i=0;i<3;i++)
        {
            var frame=socket.AsSpan(i*72,72); var ambe=Ambe.Decode(frame);
            string? mi=s.Cipher is { Synchronized: true } cipher ? cipher.MessageIndicator.ToString("X8") : null;
            string decryption=s.Cipher?.Status ?? (s.Privacy=="not_indicated" ? "not_required" : s.DecryptionStatus);
            var clear=s.Cipher?.Transform(ambe.Payload,ambe.ChannelDecodeValid);
            if(decryption=="ready") decryption=clear!=null ? "decrypted_unverified" : "channel_decode_failed";
            var data=new Dictionary<string,object?> {
                ["burst_id"]=id,["superframe_phase"]="ABCDEF"[s.Phase].ToString(),["frame_index_in_burst"]=i,
                ["frame_sequence"]=s.Frame,["playout_offset_ms"]=s.Frame*20,["duration_ms"]=20,
                ["representation"]="dmr_voice72_air",["codec_profile"]="ambe_3600x2450_v1",["bit_length"]=72,
                ["payload_hex"]=Bits.Hex(frame),["privacy_status"]=s.Privacy,["metadata_revision"]=s.Revision,
                ["colour_code"]=s.Colour,["source_id"]=s.Source,["destination_id"]=s.Destination,
                ["channel_decode_valid"]=ambe.ChannelDecodeValid,["corrected_bits"]=ambe.CorrectedBits,
                ["voice_crc_available"]=false,["erasure"]=false,
                ["ambe49_hex"]=clear!=null ? Bits.Hex(clear) : s.Privacy=="not_indicated" && s.StandardFeature && ambe.ChannelDecodeValid ? Bits.Hex(ambe.Payload) : null,
                ["ambe49_status"]=clear!=null ? "decrypted_unverified" : s.Privacy=="indicated"?"opaque_privacy":s.UnknownFeature?"opaque_vendor_profile":!s.StandardFeature?"codec_profile_unknown":s.Privacy=="unknown"?"privacy_unknown":ambe.ChannelDecodeValid?"unprotected_bits_unverified":"channel_decode_failed",
                ["privacy_profile"]=s.PrivacyProfile,["privacy_fid"]=s.PrivacyFid,["privacy_algorithm_id"]=s.AlgorithmId,
                ["privacy_key_id"]=s.KeyId,["privacy_message_indicator"]=mi,["decryption_status"]=decryption
            };
            Emit("vocoder_frame",sample,track,data); s.Frame++;
        }
    }
    public void Erasure(long sample,int track)
    {
        var s=tracks[track]; if (!s.VoiceActive) return;
        s.Phase=(s.Phase+1)%6; s.Embedded.Clear();
        s.Cipher?.AlignBurst(s.Phase,false);
        for(int i=0;i<3;i++) { s.Cipher?.SkipFrame(); Emit("erasure",sample,track,new() { ["frame_sequence"]=s.Frame,["playout_offset_ms"]=20*s.Frame,["duration_ms"]=20,["reason"]="missing_burst" }); s.Frame++; }
    }
    public void ProcessCach(ReadOnlySpan<byte> bits,long sample)
    {
        var (t,p)=Burst.DecodeCach(bits);
        if (!t.Valid) { shortLc.Clear(); return; }
        int lcss=(int)(t.Data&3);
        if(lcss==1) shortLc.Clear();
        if(lcss==1 || (lcss is 2 or 3 && shortLc.Count>0)) shortLc.AddRange(p);
        if(lcss==2)
        {
            if(shortLc.Count==68)
            {
                var d=ProductCodes.DecodeShortLc(shortLc.ToArray());
                var data=new Dictionary<string,object?> { ["pdu_type"]="short_lc",["raw_hex"]=Bits.Hex(d.Data),["bit_length"]=28,["integrity_valid"]=d.Valid,["corrected_bits"]=d.Corrections };
                if(d.Valid && Bits.UInt(d.Data.AsSpan(0,4))==1)
                { data["slot1_activity"]=Bits.UInt(d.Data.AsSpan(4,4)); data["slot2_activity"]=Bits.UInt(d.Data.AsSpan(8,4)); data["slot1_destination_hash"]=Bits.UInt(d.Data.AsSpan(12,8)); data["slot2_destination_hash"]=Bits.UInt(d.Data.AsSpan(20,8)); }
                Emit("pdu",sample,-1,data);
            }
            shortLc.Clear();
        }
        if(shortLc.Count>68) shortLc.Clear();
    }
    private void Start(int track,long sample,string reason)
    {
        var s=tracks[track]; if(s.Session!=null) return;
        s.Session=$"{captureId}:{++sessions}"; s.Frame=0; s.Revision=0;
        Emit("call_start",sample,track,new() { ["reason"]=reason,["source_id"]=null,["destination_id"]=null });
    }
    private void End(int track,long sample,string reason)
    {
        var s=tracks[track];
        if(s.Session!=null) Emit("call_end",sample,track,new() { ["reason"]=reason,["frames"]=s.Frame });
        s.Session=null; s.Colour=null; s.Source=null; s.Destination=null; s.CallType=null;
        s.Privacy="unknown"; s.Phase=-1; s.Revision=0; s.Frame=0; s.VoiceActive=false;
        s.UnknownFeature=false; s.StandardFeature=false; s.Embedded.Clear(); s.Metadata.Clear();
        ClearPrivacyContext(s);
    }
    private void Update(int track,long sample,Dictionary<string,object?> fields)
    {
        var s=tracks[track]; s.Revision++;
        foreach(var pair in fields) s.Metadata[pair.Key]=pair.Value;
        fields["metadata_revision"]=s.Revision; fields["colour_code"]=s.Colour;
        Emit("call_update",sample,track,fields);
    }
    private static void ClearPrivacyContext(TrackState s)
    {
        s.Cipher=null; s.PrivacyProfile=null; s.PrivacyFid=null; s.AlgorithmId=null; s.KeyId=null;
        s.DecryptionStatus="missing_parameters";
    }
    private void ParsePrivacyHeader(byte[] payload,int track,long sample,int colourCode)
    {
        Start(track,sample,"pi_header"); var s=tracks[track]; ClearPrivacyContext(s);
        s.Privacy="indicated"; s.Colour=colourCode;
        var bytes=Bits.Pack(payload); s.PrivacyFid=bytes[1];
        uint? mi=null;
        if(bytes[1]==0x10)
        {
            s.AlgorithmId=bytes[0]; s.KeyId=bytes[2];
            mi=BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(3,4));
            if(bytes[0] is 0x21 or 0x01)
            {
                s.PrivacyProfile="dmra_arc4";
                s.Cipher=new(bytes[2],mi.Value,privacyKeys);
            }
        }
        s.DecryptionStatus=s.Cipher?.Status ?? "unsupported_profile";
        Update(track,sample,new() { ["privacy_status"]=s.Privacy,["privacy_header_raw"]=Bits.Hex(payload),
            ["privacy_fid"]=s.PrivacyFid,["privacy_algorithm_id"]=s.AlgorithmId,["privacy_key_id"]=s.KeyId,
            ["privacy_message_indicator"]=mi?.ToString("X8"),["privacy_profile"]=s.PrivacyProfile,
            ["decryption_status"]=s.DecryptionStatus,["provenance"]="pi_header" });
    }
    private void ApplyLc(byte[] lc,int track,long sample,string provenance)
    {
        var b=Bits.Pack(lc); int op=b[0]&63, fid=b[1];
        if(fid!=0 || op is not (0 or 3) || (b[0]&0x80)!=0)
        {
            Start(track,sample,"link_control"); var opaque=tracks[track]; opaque.UnknownFeature=true; opaque.StandardFeature=false;
            if(opaque.Privacy!="indicated") opaque.Privacy="unknown_vendor";
            opaque.Source=null; opaque.Destination=null; opaque.CallType=null;
            Update(track,sample,new() { ["fid"]=fid,["flco"]=op,["protect_flag"]=(b[0]&0x80)!=0,
                ["raw_lc_hex"]=Convert.ToHexString(b),["privacy_status"]=opaque.Privacy,
                ["source_id"]=null,["destination_id"]=null,["interpretation"]="opaque_feature_set",["provenance"]=provenance });
            return;
        }
        int source=(b[6]<<16)|(b[7]<<8)|b[8], destination=(b[3]<<16)|(b[4]<<8)|b[5];
        var s=tracks[track];
        if(s.Source.HasValue && (s.Source!=source || s.Destination!=destination))
        {
            int phase=s.Phase; bool active=s.VoiceActive; int? colour=s.Colour;
            End(track,sample,"identity_changed"); s=tracks[track]; s.Phase=phase; s.VoiceActive=active; s.Colour=colour;
        }
        Start(track,sample,"link_control"); s.UnknownFeature=false; s.StandardFeature=true; s.Source=source; s.Destination=destination; s.CallType=op==0?"group":"private";
        if((b[2]&0x40)!=0) s.Privacy="indicated";
        else if(s.Privacy!="indicated") s.Privacy="not_indicated";
        Update(track,sample,new() { ["source_id"]=source,["destination_id"]=destination,["call_type"]=s.CallType,
            ["fid"]=fid,["flco"]=op,["service_options_raw"]=b[2],["emergency"]=(b[2]&0x80)!=0,
            ["broadcast"]=(b[2]&8)!=0,["ovcm"]=(b[2]&4)!=0,["priority"]=b[2]&3,
            ["privacy_status"]=s.Privacy,["provenance"]=provenance });
    }
    private void ParseCsbk(byte[] bits,int track,long sample)
    {
        var b=Bits.Pack(bits); int op=b[0]&63;
        string name=op switch { 4=>"unit_voice_request",5=>"unit_voice_response",7=>"channel_timing",38=>"negative_ack",56=>"bs_outbound_activation",61=>"preamble",_=>"unknown" };
        var data=new Dictionary<string,object?> { ["opcode"]=op,["fid"]=b[1],["csbk_name"]=b[1]==0?name:"vendor_specific",["raw_hex"]=Convert.ToHexString(b),["provenance"]="csbk" };
        Emit("control",sample,track,data);
    }
    private void ParseDataHeader(byte[] bits,int track,long sample)
    {
        var b=Bits.Pack(bits); int dpf=b[0]&15;
        var data=new Dictionary<string,object?> { ["dpf"]=dpf,["raw_hex"]=Convert.ToHexString(b),["provenance"]="data_header" };
        if(dpf is 0 or 1 or 2 or 3 or 13 or 14)
        {
            data["group"]=(b[0]&0x80)!=0; data["sap"]=(b[1]>>4)&15;
            data["destination_id"]=(b[2]<<16)|(b[3]<<8)|b[4]; data["source_id"]=(b[5]<<16)|(b[6]<<8)|b[7];
        }
        Emit("data_header",sample,track,data);
    }
    public void Reset(long sample,string reason="discontinuity")
    {
        for(int i=0;i<2;i++) { End(i,sample,reason); tracks[i]=new(); }
        shortLc.Clear();
    }
    public void Complete(long sample) => Reset(sample,"eof");
}

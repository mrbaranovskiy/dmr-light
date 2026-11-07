namespace Dmr.Protocol;

/// <summary>Standard metadata link control: talker alias header and blocks (FID 0, FLCO 4 to 7) and GPS info (FLCO 8),
/// TS 102 361-2. These LCs alternate with the call's voice LC and do not change the call's identity.</summary>
public sealed partial class ProtocolDecoder
{
    /// <summary>True for an unprotected FID 0 LC with FLCO 4 to 8. <paramref name="lc"/> is at least the 72 LC bits.</summary>
    public static bool IsMetadataLc(ReadOnlySpan<byte> lc)
    {
        int flco = (int)Bits.UInt(lc.Slice(2, 6)), fid = (int)Bits.UInt(lc.Slice(8, 8));
        return lc[0] == 0 && fid == 0 && flco is >= 4 and <= 8;
    }

    private void ApplyMetadataLc(byte[] b,int op,int track,long sample,string provenance)
    {
        var s=tracks[track];
        if(op==8)
        {
            string raw=Convert.ToHexString(b);
            if(raw==s.LastGpsHex) return;
            s.LastGpsHex=raw;
            int error=(b[2]>>1)&7;
            int lon=((b[2]&1)<<24)|(b[3]<<16)|(b[4]<<8)|b[5]; if((lon&0x1000000)!=0) lon-=0x2000000;
            int lat=(b[6]<<16)|(b[7]<<8)|b[8]; if((lat&0x800000)!=0) lat-=0x1000000;
            Emit("location",sample,track,new() {
                ["latitude"]=Math.Round(lat*180.0/(1<<24),7),["longitude"]=Math.Round(lon*360.0/(1<<25),7),
                ["position_error"]=error,["source"]="lc_gps",["source_id"]=s.Source,["raw_hex"]=raw,["provenance"]=provenance });
            return;
        }
        if(!s.Alias.Accept(op-4,b.AsSpan(2,7))) return;
        Emit("talker_alias",sample,track,new() {
            ["source_id"]=s.Source,["alias"]=s.Alias.Text,["format"]=TalkerAliasAssembler.FormatNames[s.Alias.Format],
            ["declared_length"]=s.Alias.DeclaredLength,["blocks_received"]=s.Alias.BlocksReceived(),["complete"]=s.Alias.Complete,
            ["raw_hex"]=s.Alias.RawHex(),["provenance"]=provenance });
    }
}

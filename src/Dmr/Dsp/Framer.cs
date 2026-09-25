using Dmr.Fec;
using Dmr.Protocol;

namespace Dmr.Dsp;

internal sealed class Framer
{
    private const int Capacity=65536;
    private readonly double[] frequency=new double[Capacity],power=new double[Capacity];
    private readonly ProtocolDecoder protocol;
    private readonly double inputRate;
    private readonly long origin;
    private readonly string polarity;
    private readonly bool emitSymbols;
    private readonly int?[] knownSlots=new int?[2];
    private long index=-1;
    private readonly List<Candidate> candidates=new();
    private bool locked;
    private double nextStart,gain,offset,symbolStep=10,lastSyncStart;
    private long nextOrdinal;
    private int misses;
    private string family="";
    private sealed record Pattern(string Family,double[] Symbols,double Mean,double Energy);
    private sealed record Candidate(double Start,Pattern Pattern,double Gain,double Offset,double Score);
    private readonly Pattern[] patterns;
    public Framer(double inputRate,long origin,ProtocolDecoder protocol,string polarity,bool emitSymbols=false)
    {
        this.inputRate=inputRate; this.origin=origin; this.protocol=protocol; this.polarity=polarity; this.emitSymbols=emitSymbols;
        patterns=new[]{"bs","ms","direct1","direct2"}.Select(f=>
        {
            var b=Burst.Sync(f+"_voice"); var symbols=Enumerable.Range(0,24).Select(i=>b[2*i]==0?3.0:-3.0).ToArray();
            double mean=symbols.Average(); return new Pattern(f,symbols,mean,symbols.Sum(x=>(x-mean)*(x-mean)));
        }).ToArray();
    }
    private double At(double t)
    {
        long n=(long)Math.Floor(t);
        if(n<0 || n+1>index || index-n>=Capacity-1) return 0;
        double f=t-n; return frequency[(int)(n&(Capacity-1))]*(1-f)+frequency[(int)((n+1)&(Capacity-1))]*f;
    }
    private double Power(double start)
    {
        double p=0; for(int i=0;i<132;i++) { long n=(long)(start+i*symbolStep); if(n>=0 && n<=index) p+=power[(int)(n&(Capacity-1))]; }
        return p/132;
    }
    private Candidate Fit(double syncStart,Pattern pattern,double step=10)
    {
        double sum=0,sum2=0,dot=0;
        for(int i=0;i<24;i++) { double v=At(syncStart+i*step); sum+=v; sum2+=v*v; dot+=v*(pattern.Symbols[i]-pattern.Mean); }
        double g=dot/pattern.Energy, o=sum/24-g*pattern.Mean;
        double variance=Math.Max(0,sum2-sum*sum/24);
        double score=variance>1?Math.Abs(dot)/Math.Sqrt(variance*pattern.Energy):0;
        if(Math.Abs(g)<200 || Math.Abs(g)>1300) score=0;
        return new(syncStart-54*step,pattern,g,o,score);
    }
    private byte[] Slice(double start,double g,double o,double step,out double error,int count=132)
    {
        var bits=new byte[count*2]; double sum=0;
        for(int i=0;i<count;i++)
        {
            double v=(At(start+i*step)-o)/g;
            int symbol=v<-2?-3:v<0?-1:v<2?1:3;
            int dibit=symbol switch {-3=>3,-1=>2,1=>0,_=>1};
            bits[2*i]=(byte)(dibit>>1); bits[2*i+1]=(byte)(dibit&1);
            sum+=Math.Min(16,(v-symbol)*(v-symbol));
        }
        error=sum/count; return bits;
    }
    private long Sample(double start)=>Math.Max(origin,origin+(long)Math.Round((start-FrontEnd.PulseDelay-.5)*inputRate/48000));
    private void SearchSync()
    {
        // During acquisition every pattern uses the same window at a fixed 10-sample step.
        // index>800 puts all 24 integral positions and their neighbors inside the ring.
        // Keep Fit's accumulation order and At's zero-weighted neighbor term.
        long first=index-231;
        double syncStart=first, sum=0,sum2=0;
        Span<double> window=stackalloc double[24];
        for(int i=0;i<window.Length;i++)
        {
            int n=(int)((first+i*10)&(Capacity-1));
            double v=frequency[n]+frequency[(n+1)&(Capacity-1)]*0.0;
            window[i]=v; sum+=v; sum2+=v*v;
        }
        double variance=Math.Max(0,sum2-sum*sum/24);
        if(!(variance>1)) return;
        foreach(var pattern in patterns)
        {
            double dot=0;
            for(int i=0;i<window.Length;i++) dot+=window[i]*(pattern.Symbols[i]-pattern.Mean);
            double g=dot/pattern.Energy;
            if(Math.Abs(g)<200 || Math.Abs(g)>1300) continue;
            double score=Math.Abs(dot)/Math.Sqrt(variance*pattern.Energy);
            if(score<.965) continue;
            // Rejected fits never need a heap-allocated candidate.
            var c=new Candidate(syncStart-540,pattern,g,sum/24-g*pattern.Mean,score);
            int existing=-1;
            for(int i=0;i<candidates.Count;i++)
                if(Math.Abs(candidates[i].Start-c.Start)<20 && candidates[i].Pattern.Family==pattern.Family)
                { existing=i; break; }
            if(existing>=0) { if(c.Score>candidates[existing].Score) candidates[existing]=c; }
            else if(candidates.Count<16) candidates.Add(c);
        }
    }
    public void Push(double f,double p)
    {
        frequency[(int)(++index&(Capacity-1))]=f; power[(int)(index&(Capacity-1))]=p;
        if(locked)
        {
            if(index>=nextStart+132*symbolStep+25) DecodeScheduled();
            return;
        }
        if(index>800) SearchSync();
        for(int i=0;i<candidates.Count;i++)
        {
            var c=candidates[i]; if(index<c.Start+1340) continue;
            var data=Slice(c.Start,-c.Gain,c.Offset,10,out _);
            var decoded=Burst.DecodeData(data);
            bool knownIdle=decoded.Type==9 && decoded.FecValid && Bits.Hex(decoded.Payload)=="FF83DF1732094ED1E7CD8A91";
            if((decoded.IntegrityValid || knownIdle) && Allowed(-c.Gain)) { Acquire(c,-c.Gain); return; }
            if(index<c.Start+4*2880+1340) continue;
            var fragments=new List<byte>(); bool valid=true; int? colour=null;
            // Candidate confirmation through QR and a complete embedded LC, no sync-only voice guesses.
            for(int j=1;j<=5 && c.Start+j*2880+1340<=index;j++)
            {
                var b=Slice(c.Start+j*2880,c.Gain,c.Offset,10,out double error);
                var emb=Burst.Emb(b); if(!emb.Valid || error>1.3) {valid=false;break;}
                int cc=(int)(emb.Data>>3); if(colour.HasValue && colour!=cc) {valid=false;break;} colour=cc;
                int lcss=(int)(emb.Data&3);
                if(lcss==1) fragments.Clear();
                if(lcss==1 || (lcss is 2 or 3 && fragments.Count>0)) fragments.AddRange(b[116..148]);
                if(lcss==2 && fragments.Count==128 && ProductCodes.DecodeEmbedded(fragments.ToArray()).Valid && Allowed(c.Gain))
                { Acquire(c,c.Gain); return; }
            }
            if(!valid || index>c.Start+5*2880+1340) { candidates.RemoveAt(i--); }
        }
    }
    private bool Allowed(double g)=>polarity=="auto" || (polarity=="normal"?g>0:g<0);
    private void Acquire(Candidate c,double selectedGain)
    {
        locked=true; nextStart=c.Start; nextOrdinal=0; gain=selectedGain; offset=c.Offset; symbolStep=10;
        family=c.Pattern.Family; lastSyncStart=c.Start; misses=0; candidates.Clear();
        Array.Clear(knownSlots);
        protocol.Emit("sync_acquired",Sample(c.Start),-1,new() { ["source_mode"]=family,["polarity"]=gain>0?"normal":"inverted",["score"]=c.Score,["residual_frequency_hz"]=offset });
        while(locked && index>=nextStart+132*symbolStep+25) DecodeScheduled();
    }
    private void DecodeScheduled()
    {
        double start=nextStart;
        Candidate? best=null;
        var eligible=family.StartsWith("direct",StringComparison.Ordinal)?patterns.Where(p=>p.Family.StartsWith("direct",StringComparison.Ordinal)):patterns.Where(p=>p.Family==family);
        foreach(var pat in eligible) for(double delta=-12;delta<=12;delta+=.5)
        {
            var c=Fit(start+54*symbolStep+delta,pat,symbolStep);
            if(best==null || c.Score>best.Score) best=c;
        }
        bool sync=best!.Score>.94;
        if(sync)
        {
            double elapsed=best.Start-lastSyncStart;
            double intervals=Math.Round(elapsed/(144*symbolStep));
            if(intervals>=1)
            {
                double measured=elapsed/(144*intervals);
                if(Math.Abs(measured-10)<.025) symbolStep=.8*symbolStep+.2*measured;
            }
            start=best.Start; lastSyncStart=start;
            gain=Math.CopySign(Math.Abs(best.Gain),gain); offset=.8*offset+.2*best.Offset;
        }
        else
        {
            // Decision-directed timing refinement on the full burst, constrained around the prediction.
            double bestCost=double.PositiveInfinity,bestDelta=0;
            for(double d=-2;d<=2;d+=.25)
            {
                Slice(start+d,gain,offset,symbolStep,out double cost);
                cost+=.025*d*d;
                if(cost<bestCost) { bestCost=cost;bestDelta=d; }
            }
            start+=bestDelta;
        }
        int track=(int)(nextOrdinal++&1);
        string burstFamily=sync?best.Pattern.Family:family;
        // Direct-mode absolute slot comes from the slot-specific sync, never from a guess.
        int? slot=sync && burstFamily=="direct1"?1:sync && burstFamily=="direct2"?2:null;
        var bits=Slice(start,gain,offset,symbolStep,out double error);
        bool voiceSync=sync && Math.Sign(best.Gain)==Math.Sign(gain);
        string kind=sync?burstFamily+(voiceSync?"_voice":"_data"):"embedded";
        long middle=(long)(start+66*symbolStep);
        bool plausible=Power(start)>1e-10 && power[(int)(middle&(Capacity-1))]>1e-10 && error<1.25 && (sync || Burst.Emb(bits).Valid || Burst.DecodeData(bits).IntegrityValid);
        if(family=="bs" && plausible)
        {
            var cach=Slice(start-12*symbolStep,gain,offset,symbolStep,out double cachError,12);
            var (tact,_)=Burst.DecodeCach(cach);
            if(tact.Valid && cachError<.7)
            {
                int observed=(int)((tact.Data>>2)&1)+1;
                if(knownSlots[track]==null && (knownSlots[1-track]==null || knownSlots[1-track]!=observed)) knownSlots[track]=observed;
                if(knownSlots[track]==observed) protocol.ProcessCach(cach,Sample(start-12*symbolStep));
                else protocol.Emit("diagnostic",Sample(start),track,new() { ["reason"]="cach_slot_conflict",["observed_slot"]=observed });
            }
            slot=knownSlots[track];
        }
        if(plausible)
        {
            if(emitSymbols)
            {
                var observations=new List<SoftSymbol>(132);
                for(int i=0;i<132;i++)
                {
                    double at=start+i*symbolStep, v=(At(at)-offset)/gain;
                    // Costs are squared normalized distances in dibit order 00,01,10,11.
                    observations.Add(new(Sample(at),v,(bits[2*i]<<1)|bits[2*i+1],[(v-1)*(v-1),(v-3)*(v-3),(v+1)*(v+1),(v+3)*(v+3)]));
                }
                protocol.Emit("symbols",Sample(start),track,new() { ["observations"]=observations,["residual_frequency_hz"]=offset,["deviation_unit_hz"]=Math.Abs(gain),["relative_power"]=Power(start) },slot);
            }
            protocol.Process(bits,Sample(start),track,slot,kind,best.Score,error);
            misses=0;
        }
        else { protocol.Erasure(Sample(start),track); misses++; }
        nextStart=start+144*symbolStep;
        if(misses>=14 || start-lastSyncStart>48000*1.2)
        {
            protocol.Emit("sync_lost",Sample(start),-1,new() { ["reason"]="tracking_timeout" });
            protocol.Reset(Sample(start),"sync_lost"); locked=false; candidates.Clear();
        }
    }
}

public sealed record SoftSymbol(long RfSampleIndex,double NormalizedValue,int Dibit,double[] Costs);

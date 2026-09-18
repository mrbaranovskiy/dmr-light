using System.Numerics;
using Dmr;
using Dmr.Dsp;
using Dmr.Fec;
using Dmr.Protocol;
using Dmr.Vocoder;

internal static class Synthetic
{
    internal sealed record Fixture(Complex[] Iq,List<string> Slot1Frames,List<string> Slot2Frames);
    public static byte[] Lc(int source=123456,int destination=91,int service=0,int fid=0) =>
        [0,(byte)fid,(byte)service,(byte)(destination>>16),(byte)(destination>>8),(byte)destination,(byte)(source>>16),(byte)(source>>8),(byte)source];
    public static byte[] Voice(byte[] lc,int phase,int cc,byte[] socket,string family="bs")
    {
        byte[] center;
        if(phase==0) center=Burst.Sync(family+"_voice");
        else
        {
            int lcss=phase==1?1:phase==4?2:phase==5?0:3;
            var emb=Codes.Qr16.EncodeBits((uint)((cc<<3)|lcss));
            var fragment=phase<5?ProductCodes.EncodeEmbedded(Bits.Unpack(lc)).AsSpan((phase-1)*32,32).ToArray():new byte[32];
            center=[..emb[..8],..fragment,..emb[8..]];
        }
        return [..socket[..108],..center,..socket[108..]];
    }
    public static Fixture Create(double sampleRate=48000,double carrier=0,double clockPpm=0,double noise=0,bool invert=false,int rounds=30,string mode="bs",int seed=1847)
    {
        var rng=new Random(seed); var symbols=new List<double>(10000);
        symbols.AddRange(new double[288]);
        var expected=new[]{new List<string>(),new List<string>()};
        var idle=Bits.Unpack(Convert.FromHexString("FF83DF1732094E D1E7CD8A91".Replace(" ","")));
        var shortLc=ProductCodes.EncodeShortLc(Bits.FromUInt(0,28));
        int physical=0;
        for(int round=0;round<rounds;round++) for(int slot=0;slot<2;slot++)
        {
            var lc=Lc(slot==0?123456:654321,slot==0?91:92);
            string family=mode=="direct"?slot==0?"direct1":"direct2":mode;
            int voiceRound=round-(slot==0?1:4); byte[] burst;
            if(voiceRound==-1) burst=Burst.MakeLc(lc,7,sync:family+"_data");
            else if(voiceRound>=0 && voiceRound<24)
            {
                var socket=new List<byte>();
                for(int v=0;v<3;v++)
                {
                    var bits=Enumerable.Range(0,49).Select(_=>(byte)rng.Next(2)).ToArray();
                    var air=Ambe.Encode(bits); socket.AddRange(air); expected[slot].Add(Bits.Hex(air));
                }
                burst=Voice(lc,voiceRound%6,7,socket.ToArray(),family);
            }
            else if(voiceRound==24) burst=Burst.MakeLc(lc,7,true,family+"_data");
            else burst=Burst.MakeData(idle,7,9,family+"_data");
            int fragment=physical%4;
            var cach=Burst.MakeCach(slot+1,fragment==0?1:fragment==3?2:3,shortLc.AsSpan(fragment*17,17));
            if(mode!="bs") symbols.AddRange(new double[12]);
            foreach(var bits in mode=="bs"?new[]{cach,burst}:new[]{burst})
                for(int i=0;i<bits.Length;i+=2) symbols.Add(((bits[i]<<1)|bits[i+1]) switch {0=>1,1=>3,2=>-1,_=>-3});
            physical++;
        }
        symbols.AddRange(new double[480]);
        // Modulate at 48 kHz, then independently sample its continuous piecewise-linear phase
        // at the requested capture rate and sampling-clock error.
        var h=Filters.RootRaisedCosine(); var shaped=new double[symbols.Count*10+h.Length];
        for(int i=0;i<symbols.Count;i++) for(int k=0;k<h.Length;k++) shaped[i*10+k]+=symbols[i]*10*h[k]*648;
        var phase=new double[shaped.Length]; double angle=0;
        for(int i=0;i<shaped.Length;i++) { angle+=2*Math.PI*shaped[i]/48000; phase[i]=angle; }
        double ratio=48000/sampleRate*(1+clockPpm/1e6); int n=(int)((phase.Length-1)/ratio);
        var iq=new Complex[n];
        for(int i=0;i<n;i++)
        {
            double pos=i*ratio; int j=(int)pos; double f=pos-j;
            double p=phase[j]*(1-f)+phase[j+1]*f+2*Math.PI*carrier*i/sampleRate;
            var z=Complex.FromPolarCoordinates(.5,p);
            if(noise>0) z+=new Complex(Gaussian(rng),Gaussian(rng))*noise;
            iq[i]=invert?Complex.Conjugate(z):z;
        }
        return new(iq,expected[0],expected[1]);
    }
    private static double Gaussian(Random rng)=>Math.Sqrt(-2*Math.Log(1-rng.NextDouble()))*Math.Cos(2*Math.PI*rng.NextDouble());
    public static List<DmrEvent> Decode(Complex[] iq,double rate=48000,double offset=0,int chunk=4096)
    {
        var events=new List<DmrEvent>(); var rx=new DmrReceiver(new(rate,offset),events.Add);
        for(int i=0;i<iq.Length;i+=chunk) rx.Push(iq.AsSpan(i,Math.Min(chunk,iq.Length-i)));
        rx.Complete(); return events;
    }
}

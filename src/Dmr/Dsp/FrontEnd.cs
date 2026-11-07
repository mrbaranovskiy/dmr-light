using System.Collections.Concurrent;
using System.Numerics;

namespace Dmr.Dsp;

/// <summary>Phase-continuous mixer, polyphase windowed-sinc channel resampler, FM discriminator,
/// and RRC receive filter. Output is a fixed 48 kHz stream; no chunk-local operations.
/// The resampler's polyphase table depends only on the sample rate and is shared by every instance at that rate.</summary>
internal sealed class FrontEnd
{
    private const int Phases=1024;
    private static readonly ConcurrentDictionary<double,double[][]> TapCache=new();
    private static readonly double[] PulseCoefficients=Filters.RootRaisedCosine();
    private static readonly double[] PowerCoefficients=Enumerable.Repeat(1.0/101,101).ToArray();
    private readonly int half, ringMask;
    private readonly double[][] taps;
    private readonly Complex[] ring;
    private readonly double step, sampleRate;
    private double mixStep;
    private readonly bool conjugate;
    private readonly Action<double,double> output;
    private readonly Fir pulse=new(PulseCoefficients);
    private readonly Fir power=new(PowerCoefficients);
    private long count=-1,lastNonFinite=-1;
    private double next,phase;
    private Complex previous;
    public const int PulseDelay=50;
    public FrontEnd(double sampleRate,double offset,bool conjugate,Action<double,double> output)
    {
        if(!double.IsFinite(sampleRate)||sampleRate<24000 || sampleRate>384000) throw new ArgumentOutOfRangeException(nameof(sampleRate),"Sample rate must be 24 to 384 kHz; channelize wider SDR streams first.");
        CheckOffset(sampleRate,offset);
        this.sampleRate=sampleRate; step=sampleRate/48000; mixStep=-2*Math.PI*offset/sampleRate; this.output=output; this.conjugate=conjugate;
        half=(int)Math.Ceiling(48*Math.Max(1,step));
        int ringSize=1; while(ringSize<4*half) ringSize*=2;
        // Mirror each write so a resampling window is contiguous even across ring wrap.
        ring=new Complex[2*ringSize]; ringMask=ringSize-1;
        taps=TapCache.GetOrAdd(sampleRate,static (rate,h)=>MakeTaps(rate,h),half);
    }
    private static void CheckOffset(double sampleRate,double offset)
    {
        if(!double.IsFinite(offset)||Math.Abs(offset)+6500>=sampleRate/2) throw new ArgumentOutOfRangeException(nameof(offset),"Channel passband must lie inside the capture.");
    }
    private static double[][] MakeTaps(double sampleRate,int half)
    {
        var taps=new double[Phases][];
        // Cutoff includes the FM spectrum, distinct from the post-discriminator RRC.
        double cutoff=6500/sampleRate;
        for(int p=0;p<Phases;p++)
        {
            taps[p]=new double[2*half]; double sum=0;
            for(int k=0;k<2*half;k++)
            {
                double t=k-(half-1)-p/(double)Phases;
                double sinc=Math.Abs(t)<1e-12?2*cutoff:Math.Sin(2*Math.PI*cutoff*t)/(Math.PI*t);
                double w=.42+.5*Math.Cos(Math.PI*t/half)+.08*Math.Cos(2*Math.PI*t/half);
                taps[p][k]=sinc*w; sum+=taps[p][k];
            }
            for(int k=0;k<2*half;k++) taps[p][k]/=sum;
        }
        return taps;
    }
    /// <summary>Returns to the state of a new instance at the same sample rate, without allocating.</summary>
    public void Reset(double offset)
    {
        CheckOffset(sampleRate,offset);
        mixStep=-2*Math.PI*offset/sampleRate;
        Array.Clear(ring); count=-1; lastNonFinite=-1; next=0; phase=0; previous=Complex.Zero;
        pulse.Reset(); power.Reset();
    }
    public void Push(Complex value)
    {
        if(conjugate) value=Complex.Conjugate(value);
        value*=Complex.FromPolarCoordinates(1,phase); phase=Math.IEEERemainder(phase+mixStep,2*Math.PI);
        int head=(int)(++count&ringMask);
        // Finite IQ can still overflow in the mixer. Check once per input sample
        // so finite windows can skip Complex's per-tap nonfinite handling.
        if(!Complex.IsFinite(value)) lastNonFinite=count;
        ring[head]=ring[head+ringMask+1]=value;
        while(next+half<=count)
        {
            long center=(long)Math.Floor(next); int p=Math.Min(Phases-1,(int)((next-center)*Phases));
            long first=center-half+1;
            // Negative sample positions are absent at startup; omit exactly those taps.
            int skip=first<0?(int)-first:0;
            ReadOnlySpan<double> coefficients=taps[p].AsSpan(skip);
            ReadOnlySpan<Complex> samples=ring.AsSpan((int)((first+skip)&ringMask),coefficients.Length);
            Complex z=Complex.Zero;
            if(lastNonFinite<first+skip)
            {
                double real=0,imaginary=0;
                for(int k=0;k<coefficients.Length;k++)
                {
                    double coefficient=coefficients[k];
                    real+=samples[k].Real*coefficient;
                    imaginary+=samples[k].Imaginary*coefficient;
                }
                z=new(real,imaginary);
            }
            else
                for(int k=0;k<coefficients.Length;k++) z+=samples[k]*coefficients[k];
            var product=z*Complex.Conjugate(previous);
            double f=product.Magnitude>1e-16?Math.Atan2(product.Imaginary,product.Real)*48000/(2*Math.PI):0;
            previous=z;
            output(pulse.Push(f),power.Push(z.Real*z.Real+z.Imaginary*z.Imaginary));
            next+=step;
        }
    }
}

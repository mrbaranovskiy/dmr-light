using System.Numerics;
using Dmr.Dsp;
using Dmr.Protocol;

namespace Dmr;

public sealed record ReceiverOptions(double SampleRate,double ChannelOffsetHz=0,bool Conjugate=false,
    string Polarity="auto",string CaptureId="capture",double? CentreFrequencyHz=null,DateTimeOffset? CaptureTime=null,bool EmitSymbols=false);

/// <summary>Streaming IQ receiver. Feed arbitrary chunks using Push; events are emitted synchronously.
/// Use Discontinuity at known sample gaps. Instances are single-consumer and independent.</summary>
public sealed class DmrReceiver
{
    private readonly ReceiverOptions options;
    private readonly ProtocolDecoder protocol;
    private FrontEnd front;
    private long sampleIndex;
    private bool complete;
    public long SamplesProcessed=>sampleIndex;
    public DmrReceiver(ReceiverOptions options,Action<DmrEvent> sink)
    {
        if(options.Polarity is not ("auto" or "normal" or "inverted")) throw new ArgumentException("Polarity must be auto, normal, or inverted.");
        this.options=options;
        protocol=new(options.SampleRate,sink,options.CaptureId);
        front=MakeFront();
        protocol.Emit("capture_info",0,-1,new() { ["sample_rate"]=options.SampleRate,["channel_offset_hz"]=options.ChannelOffsetHz,
            ["centre_frequency_hz"]=options.CentreFrequencyHz,["capture_time"]=options.CaptureTime,["processing_sample_rate"]=48000 });
    }
    private FrontEnd MakeFront()
    {
        var framer=new Framer(options.SampleRate,sampleIndex,protocol,options.Polarity,options.EmitSymbols);
        return new(options.SampleRate,options.ChannelOffsetHz,options.Conjugate,framer.Push);
    }
    public void Push(ReadOnlySpan<Complex> iq)
    {
        if(complete) throw new InvalidOperationException("Receiver is complete.");
        foreach(var sample in iq)
        {
            if(!double.IsFinite(sample.Real)||!double.IsFinite(sample.Imaginary)) throw new ArgumentException("Non-finite IQ sample.");
            front.Push(sample); sampleIndex++;
        }
    }
    public void Discontinuity(long missingSamples)
    {
        if(complete || missingSamples<0) throw new InvalidOperationException("Invalid discontinuity.");
        protocol.Reset(sampleIndex,"discontinuity"); sampleIndex+=missingSamples;
        protocol.Emit("sync_lost",sampleIndex,-1,new() { ["reason"]="discontinuity",["missing_samples"]=missingSamples });
        front=MakeFront();
    }
    public void Complete()
    {
        if(complete) return;
        // No invented samples at EOF: a burst that still needs future filter context remains incomplete.
        protocol.Complete(sampleIndex); complete=true;
    }
}

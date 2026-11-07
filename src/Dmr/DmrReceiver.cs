using System.Numerics;
using Dmr.Dsp;
using Dmr.Protocol;
using Dmr.Privacy;

namespace Dmr;

public sealed record ReceiverOptions(double SampleRate,double ChannelOffsetHz=0,bool Conjugate=false,
    string Polarity="auto",string CaptureId="capture",double? CentreFrequencyHz=null,DateTimeOffset? CaptureTime=null,bool EmitSymbols=false,
    Arc4Keyring? PrivacyKeys=null);

/// <summary>What changes when a receiver is reused for another capture or channel (see <see cref="DmrReceiver.Reset"/>).
/// The sample rate, polarity, conjugation, symbol output and keys stay those the receiver was created with.</summary>
public sealed record ReceiverResetOptions(string CaptureId,double ChannelOffsetHz=0,double? CentreFrequencyHz=null,DateTimeOffset? CaptureTime=null);

/// <summary>Streaming IQ receiver. Feed arbitrary chunks using Push; events are emitted synchronously.
/// Use Discontinuity at known sample gaps. Instances are single-consumer and independent.
/// An instance can be reused with Reset, which costs no allocation beyond its capture_info event, so a
/// caller can keep a pool of receivers instead of constructing one per transmission.</summary>
public sealed class DmrReceiver
{
    private ReceiverOptions options;
    private readonly ProtocolDecoder protocol;
    private readonly Framer framer;
    private readonly FrontEnd front;
    private long sampleIndex;
    private bool complete;
    public long SamplesProcessed=>sampleIndex;
    public DmrReceiver(ReceiverOptions options,Action<DmrEvent> sink)
    {
        if(options.Polarity is not ("auto" or "normal" or "inverted")) throw new ArgumentException("Polarity must be auto, normal, or inverted.");
        this.options=options;
        protocol=new(options.SampleRate,sink,options.CaptureId,options.PrivacyKeys);
        framer=new Framer(options.SampleRate,0,protocol,options.Polarity,options.EmitSymbols);
        front=new(options.SampleRate,options.ChannelOffsetHz,options.Conjugate,framer.Push);
        EmitCaptureInfo();
    }
    private void EmitCaptureInfo() =>
        protocol.Emit("capture_info",0,-1,new() { ["sample_rate"]=options.SampleRate,["channel_offset_hz"]=options.ChannelOffsetHz,
            ["centre_frequency_hz"]=options.CentreFrequencyHz,["capture_time"]=options.CaptureTime,["processing_sample_rate"]=48000 });
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
        framer.Reset(sampleIndex); front.Reset(options.ChannelOffsetHz);
    }
    public void Complete()
    {
        if(complete) return;
        // No invented samples at EOF: a burst that still needs future filter context remains incomplete.
        protocol.Complete(sampleIndex); complete=true;
    }
    /// <summary>
    /// Makes this receiver equivalent to a new one created with the same options except <paramref name="reset"/>'s:
    /// the events that follow (capture_info first) are those a new receiver would emit for the same input.
    /// A receiver that is not complete is completed first, so its open calls end with reason "eof".
    /// </summary>
    public void Reset(ReceiverResetOptions reset)
    {
        ArgumentNullException.ThrowIfNull(reset);
        Complete();
        front.Reset(reset.ChannelOffsetHz);
        options=options with { CaptureId=reset.CaptureId,ChannelOffsetHz=reset.ChannelOffsetHz,CentreFrequencyHz=reset.CentreFrequencyHz,CaptureTime=reset.CaptureTime };
        protocol.Restart(reset.CaptureId); framer.Reset(0);
        sampleIndex=0; complete=false;
        EmitCaptureInfo();
    }
}

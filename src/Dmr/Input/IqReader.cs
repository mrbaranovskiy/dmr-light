using System.Buffers.Binary;
using System.Numerics;

namespace Dmr.Input;

/// <summary>Bounded-memory RIFF WAV IQ or explicit raw IQ reader. WAV channels are I then Q.</summary>
public sealed class IqReader : IDisposable
{
    private readonly Stream stream;
    private readonly byte[] buffer;
    private readonly string format;
    private readonly int componentBytes;
    private long remaining;
    public int SampleRate { get; }
    public long Samples { get; }
    public string Format => format;
    public IqReader(string path,string format="wav",int? sampleRate=null)
    {
        stream=File.OpenRead(path);
        try
        {
            if(format=="wav")
            {
                using var r=new BinaryReader(stream,System.Text.Encoding.ASCII,true);
                if(new string(r.ReadChars(4))!="RIFF") throw new InvalidDataException("Expected little-endian RIFF WAV.");
                long riffEnd=8L+r.ReadUInt32();
                if(new string(r.ReadChars(4))!="WAVE" || riffEnd>stream.Length) throw new InvalidDataException("Invalid or truncated RIFF.");
                int tag=0,channels=0,bits=0,rate=0,align=0; long dataOffset=-1, dataLength=0;
                while(stream.Position+8<=riffEnd)
                {
                    string id=new(r.ReadChars(4)); uint size=r.ReadUInt32(); long start=stream.Position;
                    if(start+size>riffEnd) throw new InvalidDataException("Truncated WAV chunk.");
                    if(id=="fmt ")
                    {
                        if(size<16) throw new InvalidDataException("Invalid WAV format chunk.");
                        tag=r.ReadUInt16(); channels=r.ReadUInt16(); rate=r.ReadInt32(); r.ReadUInt32(); align=r.ReadUInt16(); bits=r.ReadUInt16();
                        if(tag==0xfffe)
                        {
                            if(size<40) throw new InvalidDataException("Invalid extensible WAV format.");
                            int extension=r.ReadUInt16(); int validBits=r.ReadUInt16(); r.ReadUInt32();
                            var subformat=new Guid(r.ReadBytes(16));
                            if(extension<22 || validBits!=bits) throw new InvalidDataException("Unsupported extensible valid-bit count.");
                            tag=subformat==new Guid("00000001-0000-0010-8000-00aa00389b71")?1:subformat==new Guid("00000003-0000-0010-8000-00aa00389b71")?3:0;
                        }
                    }
                    else if(id=="data" && dataOffset<0) { dataOffset=start; dataLength=size; }
                    stream.Position=start+size+(size&1);
                }
                if(channels!=2 || rate<=0 || dataOffset<0) throw new InvalidDataException("WAV IQ must have two channels, a sample rate, and a data chunk.");
                this.format=(tag,bits) switch { (1,16)=>"cs16",(1,24)=>"cs24",(1,32)=>"cs32",(3,32)=>"cf32",_=>throw new InvalidDataException("Supported WAV IQ: stereo PCM16/24/32 or float32.") };
                componentBytes=bits/8;
                if(align!=componentBytes*2 || dataLength%align!=0) throw new InvalidDataException("Invalid WAV block alignment.");
                SampleRate=rate; remaining=dataLength; stream.Position=dataOffset;
                if(sampleRate.HasValue && sampleRate!=rate) throw new ArgumentException("Explicit sample rate conflicts with WAV header.");
            }
            else
            {
                this.format=format;
                componentBytes=format switch { "cf32" or "cs32"=>4,"cs16"=>2,"cu8"=>1,_=>throw new ArgumentException("Raw format must be cf32, cs32, cs16, or cu8.") };
                SampleRate=sampleRate is >0 ? sampleRate.Value : throw new ArgumentException("Raw IQ requires --sample-rate.");
                remaining=stream.Length;
            }
            if(remaining%(componentBytes*2)!=0) throw new InvalidDataException("Truncated IQ sample pair.");
            Samples=remaining/(componentBytes*2); buffer=new byte[16384*componentBytes*2];
        }
        catch { stream.Dispose(); throw; }
    }
    public int Read(Span<Complex> destination)
    {
        int count=(int)Math.Min(Math.Min(destination.Length,16384),remaining/(componentBytes*2));
        int length=count*componentBytes*2;
        stream.ReadExactly(buffer.AsSpan(0,length)); remaining-=length;
        for(int i=0;i<count;i++)
        {
            double a=Component(buffer.AsSpan(2*i*componentBytes,componentBytes));
            double b=Component(buffer.AsSpan((2*i+1)*componentBytes,componentBytes));
            if(!double.IsFinite(a)||!double.IsFinite(b)) throw new InvalidDataException("IQ contains non-finite samples.");
            destination[i]=new(a,b);
        }
        return count;
    }
    private double Component(ReadOnlySpan<byte> b) => format switch
    {
        "cf32"=>BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(b)),
        "cs32"=>BinaryPrimitives.ReadInt32LittleEndian(b)/2147483648.0,
        "cs24"=>((b[0]|(b[1]<<8)|(b[2]<<16))<<8)/2147483648.0,
        "cs16"=>BinaryPrimitives.ReadInt16LittleEndian(b)/32768.0,
        "cu8"=>(b[0]-127.5)/128.0,
        _=>throw new InvalidOperationException()
    };
    public void Dispose()=>stream.Dispose();
}

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Dmr;
using Dmr.Input;

try
{
    if(args.Length==0 || args.Contains("--help"))
    {
        Console.WriteLine("""
        DMR recorded-IQ receiver (.NET 8)
        Usage: dmr <input> [--format wav|cf32|cs16|cs32|cu8] [--sample-rate Hz]
                   [--offset Hz] [--output events.jsonl] [--chunk-size 8192]
                   [--polarity auto|normal|inverted] [--conjugate] [--symbols]
                   [--centre-frequency Hz] [--capture-time ISO8601]
        WAV input must be stereo IQ (I then Q), not discriminator audio.
        Raw input is interleaved little-endian and requires --sample-rate.
        JSONL is written to stdout unless --output is supplied; summary goes to stderr.
        """);
        return 0;
    }
    string input=args[0]; var opt=new Dictionary<string,string>(); bool conjugate=false,symbols=false;
    string[] known=["--format","--sample-rate","--offset","--output","--chunk-size","--polarity","--centre-frequency","--capture-time"];
    for(int i=1;i<args.Length;i++)
    {
        if(args[i]=="--conjugate") { conjugate=true; continue; }
        if(args[i]=="--symbols") { symbols=true; continue; }
        if(!known.Contains(args[i]) || i+1>=args.Length) throw new ArgumentException($"Unknown or incomplete option: {args[i]}");
        if(!opt.TryAdd(args[i],args[++i])) throw new ArgumentException("Repeated option.");
    }
    string Get(string key,string fallback)=>opt.GetValueOrDefault(key,fallback);
    double Number(string key,double fallback)=>double.Parse(Get(key,fallback.ToString(CultureInfo.InvariantCulture)),CultureInfo.InvariantCulture);
    int? rate=opt.ContainsKey("--sample-rate")?checked((int)Number("--sample-rate",0)):null;
    int chunk=checked((int)Number("--chunk-size",8192)); if(chunk<1 || chunk>1_048_576) throw new ArgumentException("Chunk size must be 1 to 1048576.");
    using var reader=new IqReader(input,Get("--format","wav"),rate);
    string? output=opt.GetValueOrDefault("--output");
    if(output!=null && Path.GetFullPath(output).Equals(Path.GetFullPath(input),StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must differ from input.");
    if(output!=null) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    using var writer=output!=null?new StreamWriter(output,false,new System.Text.UTF8Encoding(false)):null;
    TextWriter target=writer ?? Console.Out;
    var counts=new Dictionary<string,int>(); int validated=0;
    var options=new ReceiverOptions(reader.SampleRate,Number("--offset",0),conjugate,Get("--polarity","auto"),Path.GetFileNameWithoutExtension(input),
        opt.ContainsKey("--centre-frequency")?Number("--centre-frequency",0):null,
        opt.ContainsKey("--capture-time")?DateTimeOffset.Parse(opt["--capture-time"],CultureInfo.InvariantCulture):null,symbols);
    var receiver=new DmrReceiver(options,e=>
    {
        target.WriteLine(e.ToJson()); counts[e.Type]=counts.GetValueOrDefault(e.Type)+1;
        if(e.Data.TryGetValue("integrity_valid",out var valid) && valid is true) validated++;
    });
    var watch=Stopwatch.StartNew(); var buffer=new Complex[chunk]; int n;
    while((n=reader.Read(buffer))>0) receiver.Push(buffer.AsSpan(0,n));
    receiver.Complete(); target.Flush();
    Console.Error.WriteLine($"{reader.Samples} IQ samples, {(double)reader.Samples/reader.SampleRate:F3}s; decoded in {watch.Elapsed.TotalSeconds:F3}s");
    Console.Error.WriteLine(string.Join(", ",counts.Select(x=>$"{x.Key}={x.Value}"))+$", validated_pdus={validated}");
    return 0;
}
catch(Exception e) when(e is ArgumentException or IOException or InvalidOperationException or OverflowException or FormatException)
{
    Console.Error.WriteLine($"dmr: {e.Message}"); return 1;
}

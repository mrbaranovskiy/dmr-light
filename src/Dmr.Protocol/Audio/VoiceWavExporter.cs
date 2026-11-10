using System.Text;

namespace Dmr.Audio;

/// <summary>Writes eligible vocoder events as separate 8 kHz mono PCM WAV calls.</summary>
public sealed class VoiceWavExporter : IDisposable
{
    private sealed class Call : IDisposable
    {
        public readonly IAmbeSpeechDecoder Decoder;
        public readonly FileStream Stream;
        public readonly BinaryWriter Writer;
        public long NextFrame;
        public int Frames;

        public Call(string path, IAmbeSpeechDecoder decoder, long firstFrame)
        {
            Decoder = decoder;
            Stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            try
            {
                Writer = new BinaryWriter(Stream, Encoding.ASCII, leaveOpen: true);
                NextFrame = firstFrame;
                Writer.Write(Encoding.ASCII.GetBytes("RIFF")); Writer.Write(0);
                Writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); Writer.Write(16);
                Writer.Write((short)1); Writer.Write((short)1); Writer.Write(8000);
                Writer.Write(16000); Writer.Write((short)2); Writer.Write((short)16);
                Writer.Write(Encoding.ASCII.GetBytes("data")); Writer.Write(0);
            }
            catch { Stream.Dispose(); throw; }
        }

        public void Write(ReadOnlySpan<short> pcm)
        {
            if (((long)Frames + 1) * 320 > uint.MaxValue - 36)
                throw new InvalidOperationException("WAV file exceeds the RIFF size limit.");
            foreach (short sample in pcm) Writer.Write(sample);
            Frames++;
        }

        public void Dispose()
        {
            long dataBytes = (long)Frames * 160 * 2;
            try
            {
                Stream.Position = 4; Writer.Write((uint)(36 + dataBytes));
                Stream.Position = 40; Writer.Write((uint)dataBytes);
            }
            finally { Writer.Dispose(); Stream.Dispose(); Decoder.Dispose(); }
        }
    }

    private readonly string directory;
    private readonly Func<IAmbeSpeechDecoder> decoderFactory;
    private readonly Dictionary<string, Call> calls = new();
    private bool disposed;
    public int FilesWritten { get; private set; }
    public int FramesWritten { get; private set; }

    public VoiceWavExporter(string directory, Func<IAmbeSpeechDecoder> decoderFactory)
    {
        this.directory = Path.GetFullPath(directory);
        this.decoderFactory = decoderFactory;
        Directory.CreateDirectory(this.directory);
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c).ToArray());
    }

    public void Accept(DmrEvent ev)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (ev.SessionId == null) return;
        string key = ev.SessionId + ":" + ev.Track;
        if (ev.Type == "call_end")
        {
            if (calls.Remove(key, out var ended)) ended.Dispose();
            return;
        }
        if (ev.Type is not ("vocoder_frame" or "erasure")) return;
        string? payload = ev.Type == "vocoder_frame" ? ev.Data.GetValueOrDefault("ambe49_hex") as string : null;
        if (!calls.TryGetValue(key, out var call))
        {
            if (payload == null) return;
            long first = Convert.ToInt64(ev.Data["frame_sequence"]);
            string stem = SafeName(ev.CaptureId + "_" + ev.SessionId + "_track" + ev.Track);
            string path = Path.Combine(directory, stem + ".wav");
            for (int suffix = 2; File.Exists(path); suffix++) path = Path.Combine(directory, stem + "_" + suffix + ".wav");
            var decoder = decoderFactory();
            try { call = new Call(path, decoder, first); }
            catch { decoder.Dispose(); throw; }
            calls.Add(key, call);
            FilesWritten++;
        }
        long sequence = Convert.ToInt64(ev.Data["frame_sequence"]);
        if (sequence < call.NextFrame) throw new InvalidOperationException("Voice frames arrived out of sequence.");
        Span<short> samples = stackalloc short[160];
        while (call.NextFrame < sequence)
        {
            samples.Clear(); call.Write(samples); call.NextFrame++; FramesWritten++;
            call.Decoder.Reset();
        }
        samples.Clear();
        if (payload != null)
            call.Decoder.Decode(Convert.FromHexString(payload), Convert.ToInt32(ev.Data["corrected_bits"]), samples);
        else call.Decoder.Reset();
        call.Write(samples); call.NextFrame++; FramesWritten++;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var call in calls.Values) call.Dispose();
        calls.Clear();
    }
}

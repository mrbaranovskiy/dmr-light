using System.Runtime.InteropServices;
using System.Text;

namespace Dmr.Audio;

/// <summary>Decodes one continuous AMBE 2450 speech stream to 8 kHz PCM.</summary>
public interface IAmbeSpeechDecoder : IDisposable
{
    void Decode(ReadOnlySpan<byte> packedPayload, int correctedBits, Span<short> pcm);
    void Reset();
}

/// <summary>Optional adapter for the mbelib 1.3 C ABI. The native library is supplied by the caller.</summary>
public sealed class MbelibSpeechDecoder : IAmbeSpeechDecoder
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Init(IntPtr current, IntPtr previous, IntPtr enhanced);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PrintVersion([Out] byte[] text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Process([Out] short[] pcm, ref int errors, ref int totalErrors,
        [Out] byte[] errorText, [In] byte[] bits, IntPtr current, IntPtr previous,
        IntPtr enhanced, int uvQuality);

    // mbelib.h 1.3: three floats/ints, five 57-element arrays, and three trailing fields.
    // All fields are 32 bits under the supported C ABI.
    private const int ParameterBytes = 1164;
    private readonly IntPtr library, current, previous, enhanced;
    private readonly Init init;
    private readonly Process process;
    private bool disposed;

    public MbelibSpeechDecoder(string libraryPath)
    {
        if (string.IsNullOrWhiteSpace(libraryPath)) throw new ArgumentException("An mbelib library path is required.");
        try
        {
            library = NativeLibrary.Load(libraryPath);
            var printVersion = Marshal.GetDelegateForFunctionPointer<PrintVersion>(NativeLibrary.GetExport(library, "mbe_printVersion"));
            var versionText = new byte[64];
            printVersion(versionText);
            int end = Array.IndexOf(versionText, (byte)0);
            string version = Encoding.ASCII.GetString(versionText, 0, end >= 0 ? end : versionText.Length);
            if (version != "1.3.0") throw new InvalidOperationException($"mbelib 1.3.0 is required; found {version}.");
            init = Marshal.GetDelegateForFunctionPointer<Init>(NativeLibrary.GetExport(library, "mbe_initMbeParms"));
            process = Marshal.GetDelegateForFunctionPointer<Process>(NativeLibrary.GetExport(library, "mbe_processAmbe2450Data"));
            current = Marshal.AllocHGlobal(ParameterBytes);
            previous = Marshal.AllocHGlobal(ParameterBytes);
            enhanced = Marshal.AllocHGlobal(ParameterBytes);
            Reset();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            Dispose();
            throw new InvalidOperationException($"Could not load mbelib from '{libraryPath}': {e.Message}", e);
        }
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        init(current, previous, enhanced);
    }

    public void Decode(ReadOnlySpan<byte> packedPayload, int correctedBits, Span<short> pcm)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (packedPayload.Length != 7 || (packedPayload[6] & 0x7f) != 0)
            throw new ArgumentException("Expected seven packed AMBE bytes with seven zero pad bits.");
        if (pcm.Length != 160) throw new ArgumentException("One AMBE frame produces 160 PCM samples.");
        if (correctedBits < 0 || correctedBits > 6) throw new ArgumentOutOfRangeException(nameof(correctedBits));
        var bits = new byte[49];
        for (int i = 0; i < 49; i++) bits[i] = (byte)((packedPayload[i / 8] >> (7 - i % 8)) & 1);
        var samples = new short[160];
        var errorText = new byte[32];
        int errors = 0, totalErrors = correctedBits;
        process(samples, ref errors, ref totalErrors, errorText, bits, current, previous, enhanced, 3);
        samples.CopyTo(pcm);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (current != IntPtr.Zero) Marshal.FreeHGlobal(current);
        if (previous != IntPtr.Zero) Marshal.FreeHGlobal(previous);
        if (enhanced != IntPtr.Zero) Marshal.FreeHGlobal(enhanced);
        if (library != IntPtr.Zero) NativeLibrary.Free(library);
    }
}

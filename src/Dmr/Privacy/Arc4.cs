using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Dmr.Tests")]

namespace Dmr.Privacy;

// Legacy on-air compatibility primitive. Instances belong to one voice track.
internal sealed class Arc4
{
    private readonly byte[] permutation = new byte[256];
    private int x, y;

    public Arc4(ReadOnlySpan<byte> key)
    {
        if (key.Length is < 1 or > 256) throw new ArgumentException("ARC4 key length must be 1 to 256 bytes.");
        for (int n = 0; n < 256; n++) permutation[n] = (byte)n;
        int j = 0;
        for (int n = 0; n < 256; n++)
        {
            j = (j + permutation[n] + key[n % key.Length]) & 255;
            (permutation[n], permutation[j]) = (permutation[j], permutation[n]);
        }
    }

    public byte Next()
    {
        x = (x + 1) & 255;
        y = (y + permutation[x]) & 255;
        (permutation[x], permutation[y]) = (permutation[y], permutation[x]);
        return permutation[(permutation[x] + permutation[y]) & 255];
    }

    public void Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        for (int n = 0; n < count; n++) Next();
    }

    /// <summary>Allocation-free keystream: discards <paramref name="drop"/> bytes, then fills <paramref name="output"/>.</summary>
    public static void Keystream(ReadOnlySpan<byte> key, int drop, Span<byte> output)
    {
        if (key.Length is < 1 or > 256) throw new ArgumentException("ARC4 key length must be 1 to 256 bytes.");
        ArgumentOutOfRangeException.ThrowIfNegative(drop);
        Span<byte> s = stackalloc byte[256];
        for (int n = 0; n < 256; n++) s[n] = (byte)n;
        int j = 0;
        for (int n = 0; n < 256; n++)
        {
            j = (j + s[n] + key[n % key.Length]) & 255;
            (s[n], s[j]) = (s[j], s[n]);
        }
        int x = 0, y = 0;
        for (int n = 0; n < drop + output.Length; n++)
        {
            x = (x + 1) & 255;
            y = (y + s[x]) & 255;
            (s[x], s[y]) = (s[y], s[x]);
            if (n >= drop) output[n - drop] = s[(s[x] + s[y]) & 255];
        }
        s.Clear();
    }
}

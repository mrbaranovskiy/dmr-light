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
}

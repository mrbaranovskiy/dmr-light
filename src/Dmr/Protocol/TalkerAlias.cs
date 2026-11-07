using System.Text;

namespace Dmr.Protocol;

/// <summary>Assembles the talker alias header (FLCO 4) and blocks 1 to 3 (FLCO 5 to 7) of one track, as specified in
/// TS 102 361-2 (Talker Alias header/block LC PDUs). Each LC carries 56 bits after FLCO and FID. The header starts with the
/// 2-bit format and the 5-bit declared length; the 7-bit format continues with seven characters, the octet formats skip one
/// unused bit and carry six octets. Each block carries 56 more bits.</summary>
public sealed class TalkerAliasAssembler
{
    public static readonly string[] FormatNames = ["7bit", "iso8", "utf8", "utf16be"];
    private readonly byte[] data = new byte[28];
    private int received;
    private int emittedLength = -1;
    private bool emittedComplete;
    public int Format { get; private set; } = -1;
    public int DeclaredLength { get; private set; }
    public string Text { get; private set; } = "";
    public bool Complete { get; private set; }
    public bool HasHeader => (received & 1) != 0;

    public void Clear()
    {
        Array.Clear(data); received = 0; emittedLength = -1; emittedComplete = false;
        Format = -1; DeclaredLength = 0; Text = ""; Complete = false;
    }

    /// <summary>Stores one LC (index 0 = header, 1 to 3 = blocks; <paramref name="info"/> = the seven octets after FID).
    /// Returns true when the decoded alias grew or became complete, i.e. when a talker_alias event is due.</summary>
    public bool Accept(int index, ReadOnlySpan<byte> info)
    {
        if (index is < 0 or > 3 || info.Length != 7) throw new ArgumentException("Invalid talker alias LC.");
        if (index == 0)
        {
            int format = info[0] >> 6, length = (info[0] >> 1) & 31;
            bool changed = HasHeader && !info.SequenceEqual(data.AsSpan(0, 7));
            if (changed && (format != Format || length != DeclaredLength))
            {
                // A different alias: forget the blocks of the previous one.
                Clear();
            }
            Format = format; DeclaredLength = length;
        }
        else if ((received & (1 << index)) != 0 && !info.SequenceEqual(data.AsSpan(index * 7, 7)))
        {
            // The same block with new content: a new alias of the same format and length. Restart from this block.
            int format = Format, length = DeclaredLength; var header = data.AsSpan(0, 7).ToArray(); bool hadHeader = HasHeader;
            Clear();
            if (hadHeader) { header.CopyTo(data, 0); received = 1; Format = format; DeclaredLength = length; }
        }
        info.CopyTo(data.AsSpan(index * 7));
        received |= 1 << index;
        Decode();
        bool due = HasHeader && (Text.Length > emittedLength || (Complete && !emittedComplete));
        if (due) { emittedLength = Text.Length; emittedComplete = Complete; }
        return due;
    }

    public int[] BlocksReceived()
    {
        var list = new List<int>(4);
        for (int i = 0; i < 4; i++) if ((received & (1 << i)) != 0) list.Add(i);
        return list.ToArray();
    }

    /// <summary>The 28 octets of header and block information in order; blocks not received are zero.</summary>
    public string RawHex() => Convert.ToHexString(data);

    /// <summary>Capacity of header plus three blocks, in declared-length units.</summary>
    public static int Capacity(int format) => format switch { 0 => 31, 1 or 2 => 27, _ => 13 };

    private void Decode()
    {
        Text = ""; Complete = false;
        if (!HasHeader) return;
        int contiguous = 0;
        while (contiguous < 3 && (received & (2 << contiguous)) != 0) contiguous++;
        int wanted = Math.Min(DeclaredLength, Capacity(Format));
        if (Format == 0)
        {
            int available = (49 + 56 * contiguous) / 7, count = Math.Min(wanted, available);
            var chars = new char[count];
            for (int c = 0; c < count; c++)
            {
                int value = 0, bit = 7 + 7 * c;
                for (int k = 0; k < 7; k++, bit++) value = (value << 1) | ((data[bit >> 3] >> (7 - (bit & 7))) & 1);
                chars[c] = (char)value;
            }
            Text = Clean(new string(chars)); Complete = count == wanted;
            return;
        }
        int octets = 6 + 7 * contiguous;
        var bytes = data.AsSpan(1, octets);
        if (Format == 1)
        {
            int count = Math.Min(wanted, octets);
            Text = Clean(Encoding.Latin1.GetString(bytes[..count])); Complete = count == wanted;
        }
        else if (Format == 2)
        {
            // The declared length counts octets (as MMDVM and the radios that generate it do); see the README.
            int count = Math.Min(wanted, octets);
            Complete = count == wanted;
            var span = bytes[..count];
            if (!Complete) span = span[..CompleteUtf8Prefix(span)];
            Text = Clean(Encoding.UTF8.GetString(span));
        }
        else
        {
            int units = Math.Min(wanted, octets / 2);
            Complete = units == wanted;
            var span = bytes[..(2 * units)];
            if (!Complete && units > 0 && char.IsHighSurrogate((char)((span[^2] << 8) | span[^1]))) span = span[..^2];
            Text = Clean(Encoding.BigEndianUnicode.GetString(span));
        }
    }

    private static int CompleteUtf8Prefix(ReadOnlySpan<byte> s)
    {
        // Drop a multi-octet sequence cut at the end of the received octets.
        for (int back = 1; back <= Math.Min(3, s.Length); back++)
        {
            byte b = s[^back];
            if ((b & 0xC0) == 0x80) continue;
            int need = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            return need > back ? s.Length - back : s.Length;
        }
        return s.Length;
    }

    private static string Clean(string text) => text.TrimEnd('\0');
}

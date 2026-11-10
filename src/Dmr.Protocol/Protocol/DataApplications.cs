using System.Globalization;
using System.Text;

namespace Dmr.Protocol;

/// <summary>A text found in a reassembled packet. <see cref="Confidence"/> is "high" when a documented or structurally
/// verified layout matched, "low" when the text was found by scanning the payload.</summary>
public sealed record TextDecode(string Text, string Encoding, string Protocol, string Confidence, string HeaderHex);

public readonly record struct UdpDatagram(string SourceIp, string DestinationIp, int SourcePort, int DestinationPort,
    int PayloadOffset, int PayloadLength, bool IpChecksumValid, bool Fragment);

/// <summary>Part 3 and vendor application decoders over reassembled packet user data. All are tolerant: they return null
/// rather than throwing on malformed input.</summary>
public static class DataApplications
{
    public const int MotorolaTmsPort = 4007, MotorolaLrrpPort = 4001, HyteraTextPort = 5016, HyteraDataPort = 3009;

    /// <summary>TS 102 361-1 table 9.50 defined data format names.</summary>
    public static string DefinedFormatName(int dd) => dd switch
    {
        0 => "binary", 1 => "bcd", 2 => "7bit",
        >= 3 and <= 17 => "iso8859-" + IsoPart(dd).ToString(CultureInfo.InvariantCulture),
        18 => "utf8", 19 => "utf16", 20 => "utf16be", 21 => "utf16le", 22 => "utf32", 23 => "utf32be", 24 => "utf32le",
        _ => "reserved"
    };

    /// <summary>ISO 8859 part number for DD values 3 to 17 (table 9.50: parts 1 to 11, then 13 to 16).</summary>
    private static int IsoPart(int dd) => dd <= 13 ? dd - 2 : dd - 1;

    /// <summary>Text of an ETSI defined short data payload (DPF 1101), or null for binary and reserved formats.</summary>
    public static (string Text, string Encoding)? DefinedShortData(int dd, ReadOnlySpan<byte> octets, int bits)
    {
        bits = Math.Clamp(bits, 0, octets.Length * 8);
        int whole = bits / 8;
        switch (dd)
        {
            case 1: return (Bcd(octets, bits / 4), "bcd");
            case 2: return (SevenBit(octets, bits / 7), "7bit");
            case >= 3 and <= 17:
            {
                int part = IsoPart(dd);
                var encoding = Iso8859(part);
                return (Clean(encoding.GetString(octets[..whole])), DefinedFormatName(dd));
            }
            case 18: return (Clean(Encoding.UTF8.GetString(octets[..whole])), "utf8");
            case 19:
            {
                var span = octets[..(whole & ~1)];
                if (span.Length >= 2 && span[0] == 0xFF && span[1] == 0xFE) return (Clean(Encoding.Unicode.GetString(span[2..])), "utf16le");
                if (span.Length >= 2 && span[0] == 0xFE && span[1] == 0xFF) span = span[2..];
                return (Clean(Encoding.BigEndianUnicode.GetString(span)), "utf16be");
            }
            case 20: return (Clean(Encoding.BigEndianUnicode.GetString(octets[..(whole & ~1)])), "utf16be");
            case 21: return (Clean(Encoding.Unicode.GetString(octets[..(whole & ~1)])), "utf16le");
            case 22 or 23: return (Clean(new UTF32Encoding(true, false).GetString(StripBom32(octets[..(whole & ~3)]))), "utf32be");
            case 24: return (Clean(new UTF32Encoding(false, false).GetString(octets[..(whole & ~3)])), "utf32le");
            default: return null;
        }
    }

    /// <summary>Text of a UDT payload (TS 102 361-1 table 9.51), or null when the format carries no text.</summary>
    public static (string Text, string Encoding)? UnifiedDataTransport(int format, ReadOnlySpan<byte> octets, int bits)
    {
        bits = Math.Clamp(bits, 0, octets.Length * 8);
        return format switch
        {
            2 => (Bcd(octets, bits / 4), "bcd"),
            3 => (SevenBit(octets, bits / 7), "7bit"),
            4 => (Clean(Encoding.Latin1.GetString(octets[..(bits / 8)])), "iso8"),
            7 => (Clean(Encoding.BigEndianUnicode.GetString(octets[..((bits / 8) & ~1)])), "utf16be"),
            _ => null
        };
    }

    public static string SevenBit(ReadOnlySpan<byte> octets, int count)
    {
        var sb = new StringBuilder(count);
        for (int c = 0; c < count; c++)
        {
            int value = 0;
            for (int k = 0; k < 7; k++) { int bit = 7 * c + k; value = (value << 1) | ((octets[bit >> 3] >> (7 - (bit & 7))) & 1); }
            sb.Append((char)value);
        }
        return Clean(sb.ToString());
    }

    private static string Bcd(ReadOnlySpan<byte> octets, int digits)
    {
        var sb = new StringBuilder(digits);
        for (int i = 0; i < digits; i++)
        {
            int nibble = (octets[i >> 1] >> ((i & 1) == 0 ? 4 : 0)) & 15;
            if (nibble <= 9) sb.Append((char)('0' + nibble));
        }
        return sb.ToString();
    }

    private static ReadOnlySpan<byte> StripBom32(ReadOnlySpan<byte> s) => s.Length >= 4 && s[0] == 0 && s[1] == 0 && s[2] == 0xFE && s[3] == 0xFF ? s[4..] : s;

    private static Encoding Iso8859(int part)
    {
        int codePage = part switch { >= 1 and <= 9 => 28590 + part, 13 => 28603, 15 => 28605, _ => 28591 };
        if (codePage == 28591) return Encoding.Latin1;
        return CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.Latin1;
    }

    private static string Clean(string text) => text.TrimEnd('\0');

    /// <summary>Parses an IPv4 datagram carrying UDP (protocol 17). Returns false for anything else.</summary>
    public static bool TryParseUdp(ReadOnlySpan<byte> p, out UdpDatagram udp)
    {
        udp = default;
        if (p.Length < 28 || (p[0] >> 4) != 4) return false;
        int ihl = (p[0] & 15) * 4;
        if (ihl < 20 || p.Length < ihl + 8 || p[9] != 17) return false;
        int total = (p[2] << 8) | p[3];
        if (total < ihl + 8) return false;
        total = Math.Min(total, p.Length);
        uint sum = 0;
        for (int i = 0; i < ihl; i += 2) sum += (uint)((p[i] << 8) | p[i + 1]);
        while (sum > 0xFFFF) sum = (sum & 0xFFFF) + (sum >> 16);
        bool fragment = (p[6] & 0x20) != 0 || (((p[6] & 0x1F) << 8) | p[7]) != 0;
        int udpLength = (p[ihl + 4] << 8) | p[ihl + 5];
        int payload = Math.Clamp(Math.Min(udpLength, total - ihl) - 8, 0, total - ihl - 8);
        udp = new($"{p[12]}.{p[13]}.{p[14]}.{p[15]}", $"{p[16]}.{p[17]}.{p[18]}.{p[19]}",
            (p[ihl] << 8) | p[ihl + 1], (p[ihl + 2] << 8) | p[ihl + 3], ihl + 8, payload, sum == 0xFFFF, fragment);
        return true;
    }

    /// <summary>Selects a text decoder for a UDP payload by port. Unknown ports only yield text when the payload is clearly text.</summary>
    public static TextDecode? UdpText(in UdpDatagram udp, ReadOnlySpan<byte> payload)
    {
        if (udp.DestinationPort == MotorolaTmsPort || udp.SourcePort == MotorolaTmsPort) return MotorolaTms(payload);
        if (udp.DestinationPort is HyteraTextPort or HyteraDataPort || udp.SourcePort is HyteraTextPort or HyteraDataPort) return Hytera(payload);
        if (udp.DestinationPort == MotorolaLrrpPort || udp.SourcePort == MotorolaLrrpPort) return null;
        return Scan(payload, "ip_udp_text", strict: true);
    }

    /// <summary>Motorola TMS (UDP 4007): 16-bit length, header 1 (bit 5 = extension, bits 4-0 = PDU type, 0 = text,
    /// 1F = acknowledgement), address length and address, extension headers while bit 7 is set, then UTF-16LE text that
    /// usually starts with CR LF. Layout from public reverse engineering, not from a Motorola specification.</summary>
    public static TextDecode? MotorolaTms(ReadOnlySpan<byte> p)
    {
        if (p.Length < 4) return null;
        int i = 2; byte first = p[i++];
        int type = first & 0x1F; bool extension = (first & 0x20) != 0;
        if (type == 0x1F) return null;
        int addressLength = p[i++];
        i += addressLength;
        if (extension) while (i < p.Length) { byte h = p[i++]; if ((h & 0x80) == 0) break; }
        bool lengthMatches = ((p[0] << 8) | p[1]) == p.Length - 2;
        if (type == 0 && i <= p.Length)
        {
            var body = p[i..];
            string text = Utf16Le(body);
            if (text.StartsWith("\r\n", StringComparison.Ordinal)) text = text[2..];
            if (lengthMatches && IsPlausible(text))
                return new(text, "utf16le", "motorola_tms", "high", Convert.ToHexString(p[..i]));
        }
        // Tolerant fallback: the text follows the first CR LF in UTF-16LE.
        for (int k = 2; k + 4 <= Math.Min(p.Length, 24); k++)
            if (p[k] == 0x0D && p[k + 1] == 0 && p[k + 2] == 0x0A && p[k + 3] == 0)
            {
                string text = Utf16Le(p[(k + 4)..]);
                if (IsPlausible(text)) return new(text, "utf16le", "motorola_tms", "low", Convert.ToHexString(p[..(k + 4)]));
            }
        return Scan(p, "motorola_tms", strict: false);
    }

    /// <summary>Hytera text message protocol (HDAP TMP), as sent on UDP 5016 (also by TYT/Baofeng MD-380 family radios) or
    /// 3009: header 09, 2-byte opcode (low octet A1 private message, B1 group message), 2-byte length, 4-byte request ID,
    /// destination and source radio IP, UTF-16LE text, checksum, footer 03. Layout from public reverse engineering.</summary>
    public static TextDecode? Hytera(ReadOnlySpan<byte> p)
    {
        if (p.Length >= 17 && p[0] == 0x09)
        {
            int service = p[2], length = (p[3] << 8) | p[4];
            if ((service & 0x0F) != 1) return null; // acknowledgements and reports carry no text
            int end = Math.Min(5 + length, p.Length);
            if (p[^1] == 0x03) end = Math.Min(end, p.Length - 2);
            if (end > 17)
            {
                string text = Utf16Le(p[17..end]);
                if (IsPlausible(text)) return new(text, "utf16le", "hytera", "high", Convert.ToHexString(p[..17]));
            }
        }
        return Scan(p, "hytera", strict: false);
    }

    /// <summary>Finds the longest plausible UTF-16LE or ASCII text run. With <paramref name="strict"/> the run must cover most
    /// of the payload, so binary payloads on unknown ports do not produce messages.</summary>
    public static TextDecode? Scan(ReadOnlySpan<byte> p, string protocol, bool strict)
    {
        int best16 = 0, start16 = 0;
        for (int parity = 0; parity < 2; parity++)
        {
            int run = 0, start = parity;
            for (int i = parity; i + 1 < p.Length; i += 2)
            {
                char c = (char)(p[i] | (p[i + 1] << 8));
                if (strict ? Common(c) : Plausible(c)) { if (run == 0) start = i; run++; if (run > best16) { best16 = run; start16 = start; } }
                else run = 0;
            }
        }
        int bestAscii = 0, startAscii = 0, current = 0;
        for (int i = 0; i < p.Length; i++)
        {
            if (p[i] is (>= 0x20 and < 0x7F) or 0x0D or 0x0A or 0x09) { current++; if (current > bestAscii) { bestAscii = current; startAscii = i - current + 1; } }
            else current = 0;
        }
        int min16 = strict ? Math.Max(3, (p.Length * 2 / 3) / 2) : 2, minAscii = strict ? Math.Max(4, p.Length * 2 / 3) : 4;
        bool ascii = bestAscii >= minAscii && bestAscii >= 2 * best16;
        if (!ascii && best16 >= min16)
        {
            string text = Utf16Le(p.Slice(start16, 2 * best16)).Trim('\0');
            if (text.Trim().Length > 0) return new(text, "utf16le", protocol, "low", Convert.ToHexString(p[..start16]));
        }
        if (ascii)
        {
            string text = Encoding.ASCII.GetString(p.Slice(startAscii, bestAscii));
            if (text.Trim().Length > 0) return new(text, "ascii", protocol, "low", Convert.ToHexString(p[..startAscii]));
        }
        return null;
    }

    private static string Utf16Le(ReadOnlySpan<byte> p) => Encoding.Unicode.GetString(p[..(p.Length & ~1)]).TrimEnd('\0');

    private static bool IsPlausible(string text)
    {
        if (text.Length == 0) return false;
        foreach (char c in text) if (!Plausible(c) && c != '\0') return false;
        return true;
    }

    /// <summary>Characters accepted when scanning payloads of unknown ports: Latin, Greek, Cyrillic, general punctuation and
    /// the euro sign. A random 16-bit value rarely falls here, so binary payloads do not look like text.</summary>
    private static bool Common(char c) => c < 0x0530 ? Plausible(c) : c is (>= '‐' and <= '‧') or '€';

    private static bool Plausible(char c)
    {
        if (c is '\r' or '\n' or '\t') return true;
        if (c < 0x20 || (c >= 0x7F && c < 0xA0) || char.IsSurrogate(c)) return false;
        if (c < 0x7F) return true;
        var category = char.GetUnicodeCategory(c);
        return category is not (UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse or UnicodeCategory.Control or UnicodeCategory.Format);
    }
}

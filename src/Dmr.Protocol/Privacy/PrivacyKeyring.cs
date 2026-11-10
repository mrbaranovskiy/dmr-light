using System.Text.Json;

namespace Dmr.Privacy;

/// <summary>
/// Known DMRA privacy keys, selected by algorithm and on-air key ID: ARC4 (5 bytes), AES-128 (16) and AES-256 (32).
/// The keyring copies supplied keys and is immutable. Key material is never emitted in events or error messages.
/// </summary>
public class PrivacyKeyring
{
    private readonly Dictionary<byte, byte[]> arc4 = new(), aes128 = new(), aes256 = new();

    public PrivacyKeyring(IReadOnlyDictionary<byte, byte[]>? arc4 = null, IReadOnlyDictionary<byte, byte[]>? aes128 = null,
        IReadOnlyDictionary<byte, byte[]>? aes256 = null)
    {
        Add(this.arc4, arc4, DmraAlgorithm.Arc4);
        Add(this.aes128, aes128, DmraAlgorithm.Aes128);
        Add(this.aes256, aes256, DmraAlgorithm.Aes256);
    }

    private static void Add(Dictionary<byte, byte[]> target, IReadOnlyDictionary<byte, byte[]>? source, DmraAlgorithm algorithm)
    {
        if (source == null) return;
        int length = DmraPrivacy.KeyLength(algorithm);
        foreach (var pair in source)
        {
            if (pair.Value == null || pair.Value.Length != length)
                throw new ArgumentException(algorithm == DmraAlgorithm.Arc4 ? "Each DMRA ARC4 key must contain exactly five bytes."
                    : $"Each DMRA {DmraPrivacy.ProfileName(algorithm)} key must contain exactly {length} bytes.");
            target.Add(pair.Key, (byte[])pair.Value.Clone());
        }
    }

    /// <summary>True when a key for this algorithm and key ID is configured.</summary>
    public bool Contains(DmraAlgorithm algorithm, byte keyId) => Keys(algorithm).ContainsKey(keyId);

    /// <summary>The configured key IDs of an algorithm, ascending.</summary>
    public IReadOnlyList<byte> KeyIds(DmraAlgorithm algorithm) => Keys(algorithm).Keys.Order().ToArray();

    internal byte[]? Find(DmraAlgorithm algorithm, byte id) => Keys(algorithm).TryGetValue(id, out var key) ? (byte[])key.Clone() : null;

    private Dictionary<byte, byte[]> Keys(DmraAlgorithm algorithm) => algorithm switch
    {
        DmraAlgorithm.Arc4 => arc4,
        DmraAlgorithm.Aes128 => aes128,
        DmraAlgorithm.Aes256 => aes256,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm), "Unknown DMRA algorithm.")
    };

    /// <summary>
    /// Reads {"dmra_arc4":[{"key_id":7,"key_hex":"0102030405"}],"dmra_aes":[{"key_id":1,"key_hex":"&lt;32 or 64 hex digits&gt;"}]}.
    /// Both sections are optional. An AES key of 32 hex digits is AES-128, of 64 digits AES-256; key IDs are unique per
    /// algorithm, so one ID may have both an AES-128 and an AES-256 key.
    /// </summary>
    public static PrivacyKeyring FromJson(string json)
    {
        // Do not include the supplied JSON, keys, or parser exception in error messages.
        const string error = "Invalid privacy key file: expected dmra_arc4 entries (key_hex of 10 hex digits) and/or dmra_aes entries " +
            "(key_hex of 32 or 64 hex digits), each with a key_id (0-255) unique per algorithm.";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException(error);
            Dictionary<byte, byte[]> arc4 = new(), aes128 = new(), aes256 = new();
            foreach (var section in root.EnumerateObject())
            {
                bool isArc4 = section.Name == "dmra_arc4";
                if ((!isArc4 && section.Name != "dmra_aes") || section.Value.ValueKind != JsonValueKind.Array) throw new ArgumentException(error);
                foreach (var entry in section.Value.EnumerateArray())
                {
                    if (!TryEntry(entry, out byte id, out string hex)) throw new ArgumentException(error);
                    var target = isArc4 ? hex.Length == 10 ? arc4 : null : hex.Length == 32 ? aes128 : hex.Length == 64 ? aes256 : null;
                    if (target == null || !target.TryAdd(id, Convert.FromHexString(hex))) throw new ArgumentException(error);
                }
            }
            return new(arc4, aes128, aes256);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        { throw new ArgumentException(error); }
    }

    private protected static bool TryEntry(JsonElement entry, out byte id, out string hex)
    {
        id = 0; hex = "";
        if (entry.ValueKind != JsonValueKind.Object || entry.EnumerateObject().Count() != 2 ||
            !entry.TryGetProperty("key_id", out var idElement) || idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetByte(out id) ||
            !entry.TryGetProperty("key_hex", out var hexElement) || hexElement.ValueKind != JsonValueKind.String || hexElement.GetString() is not { } value)
            return false;
        hex = value;
        return true;
    }
}

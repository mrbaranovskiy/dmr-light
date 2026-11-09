using System.Text.Json;

namespace Dmr.Privacy;

/// <summary>Known DMRA ARC4 keys only, selected by the on-air key ID (the original keyring; still accepted wherever a
/// <see cref="PrivacyKeyring"/> is). Key material is never emitted in events.</summary>
public sealed class Arc4Keyring : PrivacyKeyring
{
    public Arc4Keyring(IReadOnlyDictionary<byte, byte[]> keys) : base(arc4: keys ?? throw new ArgumentNullException(nameof(keys))) { }

    internal byte[]? Find(byte id) => Find(DmraAlgorithm.Arc4, id);

    /// <summary>Reads {"dmra_arc4":[{"key_id":7,"key_hex":"0102030405"}]}. Use <see cref="PrivacyKeyring.FromJson"/> for AES keys.</summary>
    public static new Arc4Keyring FromJson(string json)
    {
        // Do not include the supplied JSON, keys, or parser exception in error messages.
        const string error = "Invalid privacy key file: expected dmra_arc4 entries with unique key_id (0-255) and key_hex (10 hex digits).";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                !root.TryGetProperty("dmra_arc4", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new ArgumentException(error);
            var keys = new Dictionary<byte, byte[]>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (!TryEntry(entry, out byte number, out string hex) || hex.Length != 10 || !keys.TryAdd(number, Convert.FromHexString(hex)))
                    throw new ArgumentException(error);
            }
            return new(keys);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        { throw new ArgumentException(error); }
    }
}

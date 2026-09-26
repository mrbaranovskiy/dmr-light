using System.Text.Json;

namespace Dmr.Privacy;

/// <summary>Known DMRA ARC4 keys, selected by the on-air key ID. Key material is never emitted in events.</summary>
public sealed class Arc4Keyring
{
    private readonly Dictionary<byte, byte[]> keys = new();

    public Arc4Keyring(IReadOnlyDictionary<byte, byte[]> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var pair in keys)
        {
            if (pair.Value is not { Length: 5 }) throw new ArgumentException("Each DMRA ARC4 key must contain exactly five bytes.");
            this.keys.Add(pair.Key, (byte[])pair.Value.Clone());
        }
    }

    internal byte[]? Find(byte id) => keys.TryGetValue(id, out var key) ? (byte[])key.Clone() : null;

    /// <summary>Reads {"dmra_arc4":[{"key_id":7,"key_hex":"0102030405"}]}.</summary>
    public static Arc4Keyring FromJson(string json)
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
                if (entry.ValueKind != JsonValueKind.Object || entry.EnumerateObject().Count() != 2 ||
                    !entry.TryGetProperty("key_id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetByte(out byte number) ||
                    !entry.TryGetProperty("key_hex", out var hex) || hex.ValueKind != JsonValueKind.String || hex.GetString() is not { Length: 10 } value ||
                    !keys.TryAdd(number, Convert.FromHexString(value)))
                    throw new ArgumentException(error);
            }
            return new(keys);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        { throw new ArgumentException(error); }
    }
}

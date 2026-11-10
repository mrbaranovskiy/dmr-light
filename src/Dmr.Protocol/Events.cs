using System.Text.Json;

namespace Dmr;

public sealed record DmrEvent(int SchemaVersion, long Sequence, string Type, string CaptureId,
    long RfSampleIndex, double RfTimeSeconds, int Track, int? Slot, string? SessionId,
    IReadOnlyDictionary<string, object?> Data)
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

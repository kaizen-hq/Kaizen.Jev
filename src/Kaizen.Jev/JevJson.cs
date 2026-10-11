using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kaizen.Jev;

/// <summary>The serializer options the client uses on the wire (snake_case, nulls omitted).</summary>
public static class JevJson
{
    /// <summary>Shared, read-only options.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Keep request bytes readable (e.g. backticks in instructions) and identical to the documented example.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }
}

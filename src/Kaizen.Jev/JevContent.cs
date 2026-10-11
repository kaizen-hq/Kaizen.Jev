using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kaizen.Jev;

/// <summary>
/// A question's <c>instructions</c> or a criterion: a string, a JSON object, or a JSON array
/// (the API's <c>string | object | array</c>). Strings convert implicitly; use <see cref="FromObject{T}"/> or
/// <see cref="FromJson(JsonElement)"/> for structured instructions that reference data by name in backticks.
/// </summary>
[JsonConverter(typeof(JevContentJsonConverter))]
public sealed class JevContent : IEquatable<JevContent>
{
    private JevContent(JsonElement value) => Value = value;

    /// <summary>The JSON value: always a string, object or array.</summary>
    public JsonElement Value { get; }

    /// <summary>True when the value is a JSON string.</summary>
    public bool IsText => Value.ValueKind == JsonValueKind.String;

    /// <summary>Text content from a string.</summary>
    public static JevContent FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(JsonSerializer.SerializeToElement(text));
    }

    /// <summary>Content from a JSON string, object or array element.</summary>
    public static JevContent FromJson(JsonElement json) => json.ValueKind is JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array
        ? new(json.Clone())
        : throw new ArgumentException($"Content must be a JSON string, object or array, got {json.ValueKind}.", nameof(json));

    /// <summary>Content parsed from JSON text (must be a string, object or array).</summary>
    public static JevContent FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        return FromJson(doc.RootElement);
    }

    /// <summary>Content serialized from any value that becomes a JSON string, object or array.</summary>
    public static JevContent FromObject<T>(T value, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return FromJson(JsonSerializer.SerializeToElement(value, options ?? JevJson.Options));
    }

    /// <summary>Implicit conversion from a string.</summary>
    public static implicit operator JevContent(string text) => FromText(text);

    internal bool IsBlank => Value.ValueKind switch
    {
        JsonValueKind.String => string.IsNullOrWhiteSpace(Value.GetString()),
        JsonValueKind.Object => !Value.EnumerateObject().Any(),
        _ => Value.GetArrayLength() == 0,
    };

    /// <inheritdoc />
    public bool Equals(JevContent? other) => other is not null && JsonElement.DeepEquals(Value, other.Value);
    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as JevContent);
    /// <inheritdoc />
    public override int GetHashCode() => IsText ? Value.GetString()!.GetHashCode(StringComparison.Ordinal) : (int)Value.ValueKind;
    /// <summary>The string for text content, otherwise the raw JSON.</summary>
    public override string ToString() => IsText ? Value.GetString()! : Value.GetRawText();
}

internal sealed class JevContentJsonConverter : JsonConverter<JevContent>
{
    public override JevContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        try { return JevContent.FromJson(doc.RootElement); }
        catch (ArgumentException ex) { throw new JsonException(ex.Message); }
    }

    public override void Write(Utf8JsonWriter writer, JevContent value, JsonSerializerOptions options) => value.Value.WriteTo(writer);
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kaizen.Jev;

/// <summary>The shape of a <see cref="JevState"/>.</summary>
public enum JevStateKind
{
    /// <summary>A single string.</summary>
    Text,
    /// <summary>A JSON object.</summary>
    Object,
    /// <summary>An array of text.</summary>
    TextArray,
}

/// <summary>
/// The state Jev evaluates questions against: a string, a JSON object, or an array of text.
/// Create one with <see cref="FromText"/>, <see cref="FromObject{T}"/>, <see cref="FromJson(JsonElement)"/> or <see cref="FromTexts"/>.
/// Strings and string arrays convert implicitly.
/// </summary>
[JsonConverter(typeof(JevStateJsonConverter))]
public sealed class JevState : IEquatable<JevState>
{
    private readonly string? _text;
    private readonly JsonElement _object;
    private readonly IReadOnlyList<string>? _texts;

    private JevState(JevStateKind kind, string? text, JsonElement obj, IReadOnlyList<string>? texts)
    {
        Kind = kind; _text = text; _object = obj; _texts = texts;
    }

    /// <summary>Which shape this state has.</summary>
    public JevStateKind Kind { get; }

    /// <summary>The string value. Throws unless <see cref="Kind"/> is <see cref="JevStateKind.Text"/>.</summary>
    public string Text => Kind == JevStateKind.Text ? _text! : throw new InvalidOperationException($"State is {Kind}, not Text.");

    /// <summary>The JSON object value. Throws unless <see cref="Kind"/> is <see cref="JevStateKind.Object"/>.</summary>
    public JsonElement Object => Kind == JevStateKind.Object ? _object : throw new InvalidOperationException($"State is {Kind}, not Object.");

    /// <summary>The text array value. Throws unless <see cref="Kind"/> is <see cref="JevStateKind.TextArray"/>.</summary>
    public IReadOnlyList<string> Texts => Kind == JevStateKind.TextArray ? _texts! : throw new InvalidOperationException($"State is {Kind}, not TextArray.");

    /// <summary>State as a single string.</summary>
    public static JevState FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(JevStateKind.Text, text, default, null);
    }

    /// <summary>State as an array of text.</summary>
    public static JevState FromTexts(IEnumerable<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var list = texts.ToArray();
        if (list.Any(t => t is null)) throw new ArgumentException("Text array state cannot contain null entries.", nameof(texts));
        return new(JevStateKind.TextArray, null, default, list);
    }

    /// <summary>State as a JSON object. The element must be a JSON object.</summary>
    public static JevState FromJson(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"Object state must be a JSON object, got {json.ValueKind}.", nameof(json));
        return new(JevStateKind.Object, null, json.Clone(), null);
    }

    /// <summary>State as a JSON object parsed from a JSON string.</summary>
    public static JevState FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        return FromJson(doc.RootElement);
    }

    /// <summary>State as a JSON object serialized from any value that serializes to an object (POCO, anonymous type, dictionary).</summary>
    public static JevState FromObject<T>(T value, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return FromJson(JsonSerializer.SerializeToElement(value, options ?? JevJson.Options));
    }

    /// <summary>Implicit conversion from a string.</summary>
    public static implicit operator JevState(string text) => FromText(text);

    /// <summary>Implicit conversion from a string array.</summary>
    public static implicit operator JevState(string[] texts) => FromTexts(texts);

    /// <inheritdoc />
    public bool Equals(JevState? other) => other is not null && Kind == other.Kind && Kind switch
    {
        JevStateKind.Text => _text == other._text,
        JevStateKind.TextArray => _texts!.SequenceEqual(other._texts!),
        _ => JsonElement.DeepEquals(_object, other._object),
    };

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as JevState);

    /// <inheritdoc />
    public override int GetHashCode() => Kind switch
    {
        JevStateKind.Text => HashCode.Combine(Kind, _text),
        JevStateKind.TextArray => HashCode.Combine(Kind, _texts!.Count),
        _ => HashCode.Combine(Kind),
    };

    /// <inheritdoc />
    public override string ToString() => JsonSerializer.Serialize(this, JevJson.Options);
}

internal sealed class JevStateJsonConverter : JsonConverter<JevState>
{
    public override JevState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return JevState.FromText(reader.GetString()!);
            case JsonTokenType.StartObject:
                using (var doc = JsonDocument.ParseValue(ref reader)) return JevState.FromJson(doc.RootElement);
            case JsonTokenType.StartArray:
                var list = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.String)
                        throw new JsonException($"State arrays must contain only strings, found {reader.TokenType}.");
                    list.Add(reader.GetString()!);
                }
                return JevState.FromTexts(list);
            default:
                throw new JsonException($"State must be a string, an object, or an array of strings; found {reader.TokenType}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, JevState value, JsonSerializerOptions options)
    {
        switch (value.Kind)
        {
            case JevStateKind.Text: writer.WriteStringValue(value.Text); break;
            case JevStateKind.Object: value.Object.WriteTo(writer); break;
            default:
                writer.WriteStartArray();
                foreach (var t in value.Texts) writer.WriteStringValue(t);
                writer.WriteEndArray();
                break;
        }
    }
}

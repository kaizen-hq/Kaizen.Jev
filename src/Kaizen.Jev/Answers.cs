using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kaizen.Jev;

/// <summary>
/// One question's answer, discriminated by <c>type</c>: <see cref="NoulAnswer"/>, <see cref="ChoiceAnswer"/>, <see cref="ScoreAnswer"/>.
/// Unrecognized types deserialize to <see cref="UnknownAnswer"/> (raw JSON kept) instead of failing, so new answer types
/// added to the API don't break existing clients.
/// </summary>
[JsonConverter(typeof(JevAnswerJsonConverter))]
public abstract record JevAnswer
{
    /// <summary>The <c>type</c> discriminator.</summary>
    [JsonIgnore]
    public abstract string Type { get; }
}

/// <summary>Answer to a <see cref="NoulQuestion"/>.</summary>
public sealed record NoulAnswer : JevAnswer
{
    /// <inheritdoc />
    [JsonIgnore] public override string Type => "noul";
    /// <summary>Probability of "yes", 0..1.</summary>
    public double Noul { get; init; }
}

/// <summary>Answer to a <see cref="ChoiceQuestion"/>.</summary>
public sealed record ChoiceAnswer : JevAnswer
{
    /// <inheritdoc />
    [JsonIgnore] public override string Type => "choice";
    /// <summary>The selected criterion label.</summary>
    public string Choice { get; init; } = "";
    /// <summary>Confidence, 0..1.</summary>
    public double Confidence { get; init; }
    /// <summary>Probability per criterion label.</summary>
    public IReadOnlyDictionary<string, double> Probabilities { get; init; } = new Dictionary<string, double>();
}

/// <summary>Answer to a <see cref="ScoreQuestion"/>. Indexes are 0-based positions in the question's criteria (lowest first).</summary>
public sealed record ScoreAnswer : JevAnswer
{
    /// <inheritdoc />
    [JsonIgnore] public override string Type => "score";
    /// <summary>Probability-weighted score on the 0..N-1 index scale.</summary>
    public double Score { get; init; }
    /// <summary>Confidence, 0..1.</summary>
    public double Confidence { get; init; }
    /// <summary>Index (as a string, e.g. <c>"0"</c>) to criterion text.</summary>
    public IReadOnlyDictionary<string, string> Legend { get; init; } = new Dictionary<string, string>();
    /// <summary>Index (as a string) to probability.</summary>
    public IReadOnlyDictionary<string, double> Probabilities { get; init; } = new Dictionary<string, double>();

    /// <summary><see cref="Score"/> rounded to the nearest index (midpoints away from zero).</summary>
    public int RoundedIndex => (int)Math.Round(Score, MidpointRounding.AwayFromZero);

    /// <summary>Legend text for <see cref="RoundedIndex"/>, or null if the legend lacks it.</summary>
    public string? RoundedLabel => GetLabel(RoundedIndex);

    /// <summary>Legend text for a 0-based index, or null.</summary>
    public string? GetLabel(int index) => Legend.TryGetValue(index.ToString(CultureInfo.InvariantCulture), out var l) ? l : null;

    /// <summary>Probability for a 0-based index (0 if absent).</summary>
    public double GetProbability(int index) => Probabilities.TryGetValue(index.ToString(CultureInfo.InvariantCulture), out var p) ? p : 0d;
}

/// <summary>An answer whose <c>type</c> this client version doesn't know. <see cref="Raw"/> holds the whole JSON object.</summary>
public sealed record UnknownAnswer : JevAnswer
{
    private readonly string _type;

    /// <summary>Creates an unknown answer.</summary>
    public UnknownAnswer(string type, JsonElement raw) { _type = type; Raw = raw.Clone(); }

    /// <inheritdoc />
    public override string Type => _type;
    /// <summary>The raw answer object, including <c>type</c>.</summary>
    public JsonElement Raw { get; }
}

internal sealed class JevAnswerJsonConverter : JsonConverter<JevAnswer>
{
    public override JevAnswer Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException($"An answer must be a JSON object, got {root.ValueKind}.");
        var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
        return type switch
        {
            "noul" => root.Deserialize<NoulAnswer>(options)!,
            "choice" => root.Deserialize<ChoiceAnswer>(options)!,
            "score" => root.Deserialize<ScoreAnswer>(options)!,
            _ => new UnknownAnswer(type, root),
        };
    }

    public override void Write(Utf8JsonWriter writer, JevAnswer value, JsonSerializerOptions options)
    {
        if (value is UnknownAnswer u) { u.Raw.WriteTo(writer); return; }
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        foreach (var p in JsonSerializer.SerializeToElement(value, value.GetType(), options).EnumerateObject())
            p.WriteTo(writer);
        writer.WriteEndObject();
    }
}

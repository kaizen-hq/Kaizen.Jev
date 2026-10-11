using System.Text.Json.Serialization;

namespace Kaizen.Jev;

/// <summary>
/// A question for Jev. Serialized with a <c>type</c> discriminator: <c>noul</c>, <c>choice</c> or <c>score</c>.
/// See https://docs.typesafe.ai/api#question-types.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(NoulQuestion), "noul")]
[JsonDerivedType(typeof(ChoiceQuestion), "choice")]
[JsonDerivedType(typeof(ScoreQuestion), "score")]
public abstract record JevQuestion
{
    private protected JevQuestion(JevContent instructions) => Instructions = instructions;

    /// <summary>What Jev should decide: a string, or a structured object/array (required, non-empty).</summary>
    [JsonPropertyOrder(-1)]
    public JevContent Instructions { get; init; }

    internal virtual void Validate(string name)
    {
        if (Instructions is null || Instructions.IsBlank)
            throw new JevValidationException($"Question '{name}': instructions are required and must be non-empty.", name);
    }
}

/// <summary>Optional descriptions of what yes (near 1) and no (near 0) mean for a <see cref="NoulQuestion"/>.</summary>
public sealed record NoulCriteria
{
    /// <summary>What a yes means (JSON <c>true</c>).</summary>
    public JevContent? True { get; init; }
    /// <summary>What a no means (JSON <c>false</c>).</summary>
    public JevContent? False { get; init; }
}

/// <summary>A yes/no question. The answer's <see cref="NoulAnswer.Noul"/> is 0..1.</summary>
public sealed record NoulQuestion(JevContent Instructions) : JevQuestion(Instructions)
{
    /// <summary>Optional yes/no descriptions.</summary>
    public NoulCriteria? Criteria { get; init; }
}

/// <summary>
/// Pick one of up to 255 options. <see cref="Criteria"/> maps each option to a rubric description
/// (string, object, array, or null for no extra detail). Insertion order is preserved on the wire.
/// </summary>
[method: JsonConstructor]
public sealed record ChoiceQuestion(JevContent Instructions, IReadOnlyDictionary<string, JevContent?> Criteria) : JevQuestion(Instructions)
{
    /// <summary>Maximum number of options per choice.</summary>
    public const int MaxOptions = 255;

    /// <summary>Convenience constructor taking plain string descriptions, keeping their order. Use the main constructor for null or structured descriptions.</summary>
    public ChoiceQuestion(JevContent instructions, IEnumerable<KeyValuePair<string, string>> criteria)
        : this(instructions, ToContent(criteria)) { }

    private static Dictionary<string, JevContent?> ToContent(IEnumerable<KeyValuePair<string, string>> criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var d = new Dictionary<string, JevContent?>();
        foreach (var (k, v) in criteria) d.Add(k, JevContent.FromText(v));
        return d;
    }

    internal override void Validate(string name)
    {
        base.Validate(name);
        if (Criteria is null || Criteria.Count == 0)
            throw new JevValidationException($"Question '{name}': choice needs at least 1 option in criteria.", name);
        if (Criteria.Count > MaxOptions)
            throw new JevValidationException($"Question '{name}': choice allows at most {MaxOptions} options, got {Criteria.Count}.", name);
    }
}

/// <summary>
/// Rate along an ordered rubric of 2..10 levels, lowest first. The answer's score is on the 0..N-1 level-index scale.
/// </summary>
public sealed record ScoreQuestion(JevContent Instructions, IReadOnlyList<JevContent> Criteria) : JevQuestion(Instructions)
{
    /// <summary>Minimum number of levels.</summary>
    public const int MinLevels = 2;
    /// <summary>Maximum number of levels the API accepts.</summary>
    public const int MaxLevels = 10;

    internal override void Validate(string name)
    {
        base.Validate(name);
        if (Criteria is null || Criteria.Count < MinLevels || Criteria.Count > MaxLevels)
            throw new JevValidationException($"Question '{name}': score needs {MinLevels}..{MaxLevels} levels in criteria, got {Criteria?.Count ?? 0}.", name);
        for (var i = 0; i < Criteria.Count; i++)
            if (Criteria[i] is null)
                throw new JevValidationException($"Question '{name}': score level {i} is null.", name);
    }
}

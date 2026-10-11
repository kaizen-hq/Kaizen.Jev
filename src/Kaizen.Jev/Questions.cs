using System.Text.Json.Serialization;

namespace Kaizen.Jev;

/// <summary>
/// A question for Jev. Serialized with a <c>type</c> discriminator: <c>noul</c>, <c>choice</c> or <c>score</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(NoulQuestion), "noul")]
[JsonDerivedType(typeof(ChoiceQuestion), "choice")]
[JsonDerivedType(typeof(ScoreQuestion), "score")]
public abstract record JevQuestion
{
    private protected JevQuestion(string instructions) => Instructions = instructions;

    /// <summary>What Jev should decide, in natural language. Required, non-empty.</summary>
    [JsonPropertyOrder(-1)]
    public string Instructions { get; init; }

    internal virtual void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(Instructions))
            throw new JevValidationException($"Question '{name}': instructions must be non-empty.", name);
    }
}

/// <summary>A yes/no question. The result's <see cref="NoulAnswer.Noul"/> is 0..1.</summary>
public sealed record NoulQuestion(string Instructions) : JevQuestion(Instructions);

/// <summary>
/// Pick one of 1..255 labels. <see cref="Criteria"/> maps each label to a description of when it applies;
/// insertion order is preserved on the wire.
/// </summary>
public sealed record ChoiceQuestion(string Instructions, IReadOnlyDictionary<string, string> Criteria) : JevQuestion(Instructions)
{
    /// <summary>Maximum number of criteria (labels) Jev accepts.</summary>
    public const int MaxCriteria = 255;

    internal override void Validate(string name)
    {
        base.Validate(name);
        if (Criteria is null || Criteria.Count == 0)
            throw new JevValidationException($"Question '{name}': choice needs at least 1 criterion.", name);
        if (Criteria.Count > MaxCriteria)
            throw new JevValidationException($"Question '{name}': choice allows at most {MaxCriteria} criteria, got {Criteria.Count}.", name);
        ValidateLabels(name, Criteria.Keys.ToList(), "criterion label");
        foreach (var (label, description) in Criteria)
            if (description is null)
                throw new JevValidationException($"Question '{name}': criterion '{label}' has a null description.", name);
    }

    internal static void ValidateLabels(string name, IReadOnlyList<string> labels, string what)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in labels)
        {
            if (string.IsNullOrWhiteSpace(l))
                throw new JevValidationException($"Question '{name}': every {what} must be non-empty.", name);
            if (!seen.Add(l))
                throw new JevValidationException($"Question '{name}': duplicate {what} '{l}'.", name);
        }
    }
}

/// <summary>
/// Rate on 2..10 ordered levels. <see cref="Criteria"/> describes each level, lowest first;
/// the answer's score is on the 0..N-1 index scale of this list.
/// </summary>
public sealed record ScoreQuestion(string Instructions, IReadOnlyList<string> Criteria) : JevQuestion(Instructions)
{
    /// <summary>Minimum number of levels.</summary>
    public const int MinCriteria = 2;
    /// <summary>Maximum number of levels.</summary>
    public const int MaxCriteria = 10;

    internal override void Validate(string name)
    {
        base.Validate(name);
        if (Criteria is null || Criteria.Count < MinCriteria || Criteria.Count > MaxCriteria)
            throw new JevValidationException($"Question '{name}': score needs {MinCriteria}..{MaxCriteria} criteria, got {Criteria?.Count ?? 0}.", name);
        ChoiceQuestion.ValidateLabels(name, Criteria, "criterion");
    }
}

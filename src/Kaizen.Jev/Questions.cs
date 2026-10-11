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

/// <summary>A yes/no question. The result's <see cref="JevQuestionResult.Noul"/> is 0..1.</summary>
public sealed record NoulQuestion(string Instructions) : JevQuestion(Instructions);

/// <summary>Pick one of 1..255 unique, non-empty options.</summary>
public sealed record ChoiceQuestion(string Instructions, IReadOnlyList<string> Options) : JevQuestion(Instructions)
{
    /// <summary>Maximum number of options Jev accepts.</summary>
    public const int MaxOptions = 255;

    internal override void Validate(string name)
    {
        base.Validate(name);
        if (Options is null || Options.Count == 0)
            throw new JevValidationException($"Question '{name}': choice needs at least 1 option.", name);
        if (Options.Count > MaxOptions)
            throw new JevValidationException($"Question '{name}': choice allows at most {MaxOptions} options, got {Options.Count}.", name);
        ValidateLabels(name, Options, "option");
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

/// <summary>Rate on 2..10 ordered levels (lowest first).</summary>
public sealed record ScoreQuestion(string Instructions, IReadOnlyList<string> Levels) : JevQuestion(Instructions)
{
    /// <summary>Minimum number of levels.</summary>
    public const int MinLevels = 2;
    /// <summary>Maximum number of levels.</summary>
    public const int MaxLevels = 10;

    internal override void Validate(string name)
    {
        base.Validate(name);
        if (Levels is null || Levels.Count < MinLevels || Levels.Count > MaxLevels)
            throw new JevValidationException($"Question '{name}': score needs {MinLevels}..{MaxLevels} levels, got {Levels?.Count ?? 0}.", name);
        ChoiceQuestion.ValidateLabels(name, Levels, "level");
    }
}

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Kaizen.Jev;

/// <summary>A request to <c>POST /v1/systemone</c>. Put all independent questions in one request; Jev evaluates them in parallel against one copy of state.</summary>
public sealed partial record JevRequest
{
    /// <summary>The default model alias.</summary>
    public const string LatestModel = "jev-latest";

    /// <summary>Model id or alias. Defaults to <c>jev-latest</c>.</summary>
    public string Model { get; init; } = LatestModel;

    /// <summary>The state the questions are about.</summary>
    public required JevState State { get; init; }

    /// <summary>Questions keyed by name. Names: 1..64 chars of letters, digits, '_' or '-'.</summary>
    public required IReadOnlyDictionary<string, JevQuestion> Questions { get; init; }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex NamePattern();

    /// <summary>Checks the request against the documented limits. Throws <see cref="JevValidationException"/>.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Model)) throw new JevValidationException("Model must be non-empty.");
        if (State is null) throw new JevValidationException("State is required.");
        if (Questions is null || Questions.Count == 0) throw new JevValidationException("At least one question is required.");
        foreach (var (name, q) in Questions)
        {
            if (name is null || !NamePattern().IsMatch(name))
                throw new JevValidationException($"Invalid question name '{name}': use 1..64 letters, digits, '_' or '-'.", name);
            if (q is null) throw new JevValidationException($"Question '{name}' is null.", name);
            q.Validate(name);
        }
    }
}

/// <summary>The response from <c>POST /v1/systemone</c>.</summary>
public sealed record JevResponse
{
    /// <summary>The concrete model that answered, e.g. <c>jev-1.13.0</c> when <c>jev-latest</c> was sent.</summary>
    public string Model { get; init; } = "";

    /// <summary>Results keyed by question name (assumed JSON field: <c>results</c>).</summary>
    public IReadOnlyDictionary<string, JevQuestionResult> Results { get; init; } = new Dictionary<string, JevQuestionResult>();

    /// <summary>Token usage.</summary>
    public JevUsage? Usage { get; init; }

    /// <summary>Request id from the response headers (<c>x-request-id</c> or <c>request-id</c>), if any. Not serialized.</summary>
    [JsonIgnore]
    public string? RequestId { get; init; }
}

/// <summary>Usage reported by Jev.</summary>
public sealed record JevUsage
{
    /// <summary>Input tokens consumed (<c>input_tokens</c>).</summary>
    public long InputTokens { get; init; }
}

/// <summary>
/// One question's result. Which fields are set depends on the question type:
/// noul sets <see cref="Noul"/>; choice sets <see cref="Choice"/>, <see cref="Probabilities"/>, <see cref="Confidence"/>;
/// score sets <see cref="Score"/>, <see cref="Probabilities"/>, <see cref="Confidence"/>. See README ASSUMPTIONS.
/// </summary>
public sealed record JevQuestionResult
{
    /// <summary>Echoed question type, if the API returns it.</summary>
    public string? Type { get; init; }
    /// <summary>noul: probability of "yes", 0..1.</summary>
    public double? Noul { get; init; }
    /// <summary>choice: the selected option.</summary>
    public string? Choice { get; init; }
    /// <summary>score: probability-weighted score.</summary>
    public double? Score { get; init; }
    /// <summary>choice/score: probability per option/level label.</summary>
    public IReadOnlyDictionary<string, double>? Probabilities { get; init; }
    /// <summary>choice/score: confidence.</summary>
    public double? Confidence { get; init; }
}

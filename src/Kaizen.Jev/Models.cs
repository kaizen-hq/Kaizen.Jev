using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;
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

    /// <summary>Answers keyed by question name.</summary>
    public IReadOnlyDictionary<string, JevAnswer> Answers { get; init; } = new Dictionary<string, JevAnswer>();

    /// <summary>Token usage.</summary>
    public JevUsage? Usage { get; init; }

    /// <summary>Request id from the response headers (<c>x-request-id</c> or <c>request-id</c>), if any. Not serialized.</summary>
    [JsonIgnore]
    public string? RequestId { get; init; }

    /// <summary>Gets the answer for <paramref name="name"/> as <typeparamref name="T"/>.</summary>
    /// <exception cref="KeyNotFoundException">No answer with that name.</exception>
    /// <exception cref="InvalidCastException">The answer has a different type.</exception>
    public T GetAnswer<T>(string name) where T : JevAnswer
    {
        if (!Answers.TryGetValue(name, out var a))
            throw new KeyNotFoundException($"No answer named '{name}'. Answers: {string.Join(", ", Answers.Keys)}.");
        return a as T ?? throw new InvalidCastException($"Answer '{name}' is {a.GetType().Name} (type '{a.Type}'), not {typeof(T).Name}.");
    }

    /// <summary>Tries to get the answer for <paramref name="name"/> as <typeparamref name="T"/>.</summary>
    public bool TryGetAnswer<T>(string name, [NotNullWhen(true)] out T? answer) where T : JevAnswer
    {
        answer = Answers.TryGetValue(name, out var a) ? a as T : null;
        return answer is not null;
    }
}

/// <summary>Usage reported by Jev.</summary>
public sealed record JevUsage
{
    /// <summary>Input tokens consumed (<c>input_tokens</c>).</summary>
    public long InputTokens { get; init; }
    /// <summary>Output tokens produced (<c>output_tokens</c>).</summary>
    public long OutputTokens { get; init; }
}

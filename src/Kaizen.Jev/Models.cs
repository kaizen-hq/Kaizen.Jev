using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Kaizen.Jev;

/// <summary>Model names accepted in <see cref="JevRequest.Model"/>. See https://docs.typesafe.ai/models.</summary>
public static class JevModels
{
    /// <summary>Most recent stable release (alias). Default.</summary>
    public const string Latest = "jev-latest";
    /// <summary>Most recent release, official or not (alias).</summary>
    public const string Preview = "jev-preview";
    /// <summary>Jev 1.13.0 (versioned id).</summary>
    public const string Jev_1_13_0 = "jev-1.13.0";
}

/// <summary>A request to <c>POST /v1/systemone</c>. Put all independent questions in one request; Jev evaluates them in parallel against one copy of state.</summary>
public sealed record JevRequest
{
    /// <summary>The default model alias.</summary>
    public const string LatestModel = JevModels.Latest;

    /// <summary>The content to evaluate.</summary>
    [JsonPropertyOrder(0)]
    public required JevState State { get; init; }

    /// <summary>Model id or alias (see <see cref="JevModels"/>). Defaults to <c>jev-latest</c>.</summary>
    [JsonPropertyOrder(1)]
    public string Model { get; init; } = LatestModel;

    /// <summary>Questions keyed by an id you choose; answers come back under the same ids. Ids are not sent to the model.</summary>
    [JsonPropertyOrder(2)]
    public required IReadOnlyDictionary<string, JevQuestion> Questions { get; init; }

    /// <summary>Checks the request against the documented limits. Throws <see cref="JevValidationException"/>.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Model)) throw new JevValidationException("Model is required.");
        if (State is null) throw new JevValidationException("State is required.");
        if (Questions is null || Questions.Count == 0) throw new JevValidationException("At least one question is required.");
        foreach (var (name, q) in Questions)
        {
            if (q is null) throw new JevValidationException($"Question '{name}' is null.", name);
            q.Validate(name);
        }
    }
}

/// <summary>Response from <c>GET /v1/models</c>.</summary>
public sealed record JevModelList
{
    /// <summary>One entry per model or alias.</summary>
    public IReadOnlyList<JevModelInfo> Models { get; init; } = [];
}

/// <summary>A model or alias your account can send in <see cref="JevRequest.Model"/>.</summary>
public sealed record JevModelInfo
{
    /// <summary>Model id or alias.</summary>
    public string Name { get; init; } = "";
    /// <summary>What the model is for.</summary>
    public string Description { get; init; } = "";
    /// <summary>Release date, as returned.</summary>
    public string ReleaseDate { get; init; } = "";
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

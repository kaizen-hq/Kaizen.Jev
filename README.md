# Kaizen.Jev

.NET 11 client for the [TypeSafe Jev API](https://docs.typesafe.ai/api). You ask typed questions about a piece of state: **noul** (yes/no), **choice** (pick one option) and **score** (rate on a rubric). Jev answers them all in parallel in one request.

API reference: <https://docs.typesafe.ai/api> · Models: <https://docs.typesafe.ai/models> · State: <https://docs.typesafe.ai/concepts/state>

## Install

```sh
dotnet add package Kaizen.Jev   # not published yet
```

## Usage

```csharp
using Kaizen.Jev;

builder.Services.AddJevClient(o =>
{
    o.ApiKey = builder.Configuration["TypeSafe:ApiKey"]!;   // required
    // o.BaseAddress = new Uri("https://api.typesafe.ai/"); // default
    // o.MaxRetries = 2;                                    // retries on 429/529, see below
});                                                         // returns IHttpClientBuilder for extra handlers

// inject IJevClient
var response = await jev.SystemOneAsync(new JevRequest
{
    State = "Hi, I've been trying to connect my Stripe account for 3 days and the integration keeps failing. I'm losing sales. Please help ASAP.",
    // Model defaults to JevModels.Latest ("jev-latest")
    Questions = new Dictionary<string, JevQuestion>
    {
        ["department"] = new ChoiceQuestion("Which team should handle this", new Dictionary<string, string>
        {
            ["billing"]   = "Payment or subscription issues",
            ["technical"] = "Bugs or integration problems",
            ["sales"]     = "Pricing or account questions",
        }),
        ["frustration"] = new ScoreQuestion("How frustrated the customer appears",
            ["Calm, just stating facts", "Frustrated but civil", "Very angry, strong language"]),
        ["is_urgent"] = new NoulQuestion("The message conveys urgency or time-sensitivity"),
    },
}, cancellationToken);

response.Model;                                  // "jev-1.13.0", the versioned model that answered

var dept = response.GetAnswer<ChoiceAnswer>("department");
dept.Choice;                                     // "technical"
dept.Confidence;                                 // 0.78
dept.Probabilities;                              // { technical: 0.85, sales: 0.0, billing: 0.15 }

var mood = response.GetAnswer<ScoreAnswer>("frustration");
mood.Score;                                      // 1.0, probability-weighted level index (0..N-1)
mood.RoundedLabel;                               // "Frustrated but civil"
mood.GetProbability(2);                          // 0.0
mood.Legend;                                     // { "0": "Calm, just stating facts", ... }

if (response.TryGetAnswer<NoulAnswer>("is_urgent", out var urgent))
    _ = urgent.Noul;                             // 1.0

response.Usage?.InputTokens;                     // 392
response.Usage?.OutputTokens;                    // 65
```

Put all independent questions in **one** request. Jev reads the state once and evaluates every question against it in parallel. Question ids are yours to choose; answers come back under the same ids, and the ids aren't sent to the model.

Without DI: `new JevClient(new HttpClient(), Options.Create(new JevClientOptions { ApiKey = key }))`.

### State

A string, a JSON object, or an array of text values:

```csharp
JevState a = "My card was charged twice.";                                       // string
var b = JevState.FromObject(new { message = "Charged twice.", order_id = "A-104" }); // object (recommended)
JevState c = new[] { "Hi", "My customer number is TS1337.", "Charged twice." };   // array of text
var d = JevState.FromJson("""{"ticket":{"subject":"Duplicate charge"}}""");      // raw JSON object
```

### Questions

`instructions` and every criterion can be a string or structured JSON (`JevContent`). Strings convert implicitly; use `JevContent.FromObject(...)` or `JevContent.FromJson(...)` for structure.

```csharp
// noul: optional descriptions of yes/no
new NoulQuestion("Does this convey urgency?")
{
    Criteria = new NoulCriteria { True = "Explicitly time-sensitive", False = "No urgency expressed" },
};

// structured instructions: refer to data fields by name in backticks
new NoulQuestion(JevContent.FromObject(new
{
    potential_duplicate = new { name = "John Smith", location = "Oakland, California" },
    question = "Is the resume for the same person as `potential_duplicate`?",
}));

// choice: option -> description (string, object, array, or null); order preserved
new ChoiceQuestion("Which team should handle this?", new Dictionary<string, JevContent?>
{
    ["billing"] = "Payments, invoicing, refunds",
    ["technical"] = null,
});

// score: ordered levels, lowest first
new ScoreQuestion("How frustrated is the customer?", ["Calm", "Frustrated", "Very angry"]);
```

### Models

`JevModels.Latest` (`jev-latest`, the default), `JevModels.Preview` (`jev-preview`) and `JevModels.Jev_1_13_0` (`jev-1.13.0`). Aliases move when new releases ship. `response.Model` always reports the versioned id, so pin that id if you've tuned thresholds against a version. `ListModelsAsync()` calls `GET /v1/models`.

## Answers

`response.Answers` holds `JevAnswer` values, selected by their `type`: `NoulAnswer` (`Noul`, 0..1), `ChoiceAnswer` (`Choice`, `Probabilities`, `Confidence`) and `ScoreAnswer` (`Score`, `Legend`, `Probabilities`, `Confidence`).

- `GetAnswer<T>(id)` throws `KeyNotFoundException` or `InvalidCastException` with a message naming the id and the actual type. `TryGetAnswer<T>(id, out var a)` doesn't throw.
- `ScoreAnswer.RoundedIndex` and `RoundedLabel` round `Score` to the nearest level (1.5 rounds to 2). `GetLabel(i)` and `GetProbability(i)` look up a level by integer index; a missing level gives `null` or `0`.
- Missing `Probabilities` or `Legend` maps come back empty rather than null.
- An answer with a `type` this version doesn't know becomes an `UnknownAnswer` (its `Type` plus the full `Raw` JSON) instead of failing the response. That keeps older clients working if the API adds answer types.

## Validation, errors and retries

Before sending, the client enforces the documented rules and throws `JevValidationException` (an `ArgumentException` with `QuestionName`):

- `model` and `state` are required, with at least one question
- `instructions` is required and must be non-empty (a blank string, `{}` or `[]` is rejected)
- choice: 1 to 255 options
- score: 2 to 10 levels, none null

Token limits (64k per request, 32k for state plus the longest question) are enforced by the server.

A non-2xx status throws `JevApiException`:

| Status | Property | Meaning (per the docs) |
|---|---|---|
| 401 | `IsUnauthorized` | Missing or invalid API key |
| 422 | `IsValidationError` | Request body failed validation; the body names the offending field |
| 429 | `IsRateLimited` | Rate limit exceeded |
| 529 | `IsOverloaded` | TypeSafe temporarily overloaded |

The exception always carries `StatusCode`, the raw `ResponseBody` (capped at 64 KB), `RetryAfter`, `Attempts` and `RequestId` (read from `x-request-id`/`request-id` when present). It also fills `ErrorMessage`, `ErrorCode` and `ErrorDetails` from the JSON body when it recognizes the shape: `{"error": "..."}`, `{"error": {"code"|"type", "message"}}`, top-level `message`/`code`, or `{"detail": ...}`. For a 422 detail list like `[{"loc": [...], "msg": ...}]`, `ErrorMessage` reads `body.questions.q.criteria: Field required` and `ErrorDetails` holds the list.

**Retries:** on 429 and 529 the client retries up to `MaxRetries` times (default 2), with exponential backoff and jitter starting at `RetryBaseDelay` (500 ms). When the response has a `retry-after` header, it waits that long instead, capped at `MaxRetryDelay` (30 s). Other statuses fail at once. Set `MaxRetries = 0` if you'd rather use your own policy, e.g. `AddJevClient(...).AddStandardResilienceHandler()`. Cancellation is honored throughout, including during backoff waits, and surfaces as `OperationCanceledException`.

## Wire format

`POST https://api.typesafe.ai/v1/systemone` with `Authorization: Bearer <API_KEY>` and `Content-Type: application/json`. Bodies are snake_case and omit nulls, except choice criteria, where `null` is meaningful. `type` comes first in each question. Request keys go `state`, `model`, `questions`, as in the docs. `JevJson.Options` exposes the serializer options. For why questions and answers are polymorphic records rather than C# 15 `union` types, see [docs/union-decision.md](docs/union-decision.md).

## Open questions

The docs say errors come with "a JSON body describing what went wrong", but they don't give its schema or any request-id header. The client handles the common shapes listed above and always keeps the raw body. Once a real error body is observed, `ErrorCode`/`ErrorMessage` mapping can be narrowed to it.

## Building

```sh
dotnet build && dotnet test
dotnet pack src/Kaizen.Jev/Kaizen.Jev.csproj -c Release -o artifacts   # Kaizen.Jev.0.1.0.nupkg + .snupkg
```

The SDK is pinned in `global.json` (.NET 11 RC1). Package versions live in `Directory.Packages.props` (central package management), and shared properties in `Directory.Build.props`. SourceLink comes from the .NET SDK. The build is deterministic, with `ContinuousIntegrationBuild` set on GitHub Actions.

If restore hangs on a sandboxed machine during package signature verification, set `NUGET_CERT_REVOCATION_MODE=offline`.

## Releasing (Trusted Publishing)

`.github/workflows/publish.yml` runs **only** when a tag matching `v*.*.*` is pushed. It builds, tests, packs with the version from the tag (`v0.1.0` gives `0.1.0`), exchanges a GitHub OIDC token for a short-lived nuget.org key via [`NuGet/login@v1`](https://github.com/NuGet/login), and pushes with `--skip-duplicate`. No long-lived API key secret is stored.

One-time setup (captain):

1. On nuget.org, open **Trusted Publishing** and add a GitHub Actions policy: owner `kaizen-hq`, repository `Kaizen.Jev`, workflow file `publish.yml`.
2. In the GitHub repo, go to Settings, then Secrets and variables, then Actions, then **Variables**, and add `NUGET_USER` set to the nuget.org account name that owns the policy. The workflow fails fast if it's empty.
3. Release: `git tag v0.1.0 && git push origin v0.1.0`.

`ci.yml` builds, tests and packs on every push to `main` and every PR.

## License

MIT © 2026 Kaizenio, Inc.

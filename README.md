# Kaizen.Jev

.NET 11 client for the Jev API (`POST /v1/systemone`). You ask typed questions about a piece of state: **noul** (yes/no), **choice** (pick one option) and **score** (ordered levels). Jev answers all of them in parallel in one request.

## Install

```sh
dotnet add package Kaizen.Jev   # not published yet
```

## Usage

```csharp
using Kaizen.Jev;

builder.Services.AddJevClient(o =>
{
    o.ApiKey = builder.Configuration["Jev:ApiKey"]!;          // required
    // o.BaseAddress = new Uri("https://api.typesafe.ai/");    // default
})
.AddStandardResilienceHandler();                               // optional: it returns IHttpClientBuilder

// inject IJevClient
var response = await jev.SystemOneAsync(new JevRequest
{
    // Model defaults to "jev-latest"
    State = "Hi, I've been trying to connect my Stripe account for 3 days and the integration keeps failing. I'm losing sales. Please help ASAP.",
    Questions = new Dictionary<string, JevQuestion>
    {
        // choice: label -> description, 1..255 labels, order preserved
        ["department"] = new ChoiceQuestion("Which team should handle this", new Dictionary<string, string>
        {
            ["billing"]   = "Payment or subscription issues",
            ["technical"] = "Bugs or integration problems",
            ["sales"]     = "Pricing or account questions",
        }),
        // score: 2..10 levels, lowest first
        ["frustration"] = new ScoreQuestion("How frustrated the customer appears",
            ["Calm, just stating facts", "Frustrated but civil", "Very angry, strong language"]),
        // noul: yes/no
        ["is_urgent"] = new NoulQuestion("The message conveys urgency or time-sensitivity"),
    },
}, cancellationToken);

response.Model;                                      // "jev-1.13.0" (concrete model for jev-latest)

var dept = response.GetAnswer<ChoiceAnswer>("department");
dept.Choice;                                         // "technical"
dept.Confidence;                                     // 0.78
dept.Probabilities;                                  // { technical: 0.85, sales: 0.0, billing: 0.15 }

var mood = response.GetAnswer<ScoreAnswer>("frustration");
mood.Score;                                          // 1.0, weighted, on the 0..N-1 index scale
mood.RoundedIndex;                                   // 1
mood.RoundedLabel;                                   // "Frustrated but civil" (from legend)
mood.GetProbability(2);                              // 0.0
mood.Legend;                                         // { "0": "Calm, just stating facts", ... }

if (response.TryGetAnswer<NoulAnswer>("is_urgent", out var urgent))
    _ = urgent.Noul;                                 // 1.0

response.Usage?.InputTokens;                         // 392
response.Usage?.OutputTokens;                        // 65
response.RequestId;                                  // from x-request-id / request-id header, if sent
```

Put all independent questions in **one** request. Jev evaluates them in parallel against a single copy of state, so you pay for the state's input tokens once.

### State

State is a string, a JSON object, or an array of text:

```csharp
JevState a = "free text";                                  // implicit
JevState b = new[] { "line 1", "line 2" };                 // implicit
var c = JevState.FromObject(new { id = 1 });               // any POCO/anonymous/dictionary
var d = JevState.FromJson("""{"id":1}""");                 // raw JSON object
```

Without DI: `new JevClient(new HttpClient { BaseAddress = ... }, Options.Create(new JevClientOptions { ApiKey = key }))`.

## Validation and errors

`SystemOneAsync` checks these limits before sending and throws `JevValidationException` (an `ArgumentException` with `QuestionName`):

- at least one question; names are 1..64 characters of `[A-Za-z0-9_-]`
- `instructions` must be non-empty
- `choice`: `criteria` is a map of label to description with 1..255 entries; labels are non-empty and descriptions non-null
- `score`: `criteria` is a list of 2..10 unique, non-empty levels, lowest first

A non-2xx status or an unreadable body throws `JevApiException` with `StatusCode`, `ResponseBody` (capped at 64 KB) and `RequestId`. Cancellation surfaces as `OperationCanceledException`. If `ApiKey` is missing, DI options validation fails with `OptionsValidationException`; a hand-built client throws `InvalidOperationException`.

## Answers

`response.Answers` holds polymorphic `JevAnswer` values, also chosen by `type`: `NoulAnswer`, `ChoiceAnswer` or `ScoreAnswer`. Read them with `GetAnswer<T>(name)`, which throws `KeyNotFoundException` or `InvalidCastException` with a clear message, or with `TryGetAnswer<T>(name, out var a)`.

**Forward compatibility:** an answer whose `type` this version doesn't know becomes an `UnknownAnswer`, carrying `Type` and the full `Raw` `JsonElement`, instead of failing the whole response. A request can only produce new answer types if you send new question types, which this version can't build, so tolerance costs nothing and keeps parsing from breaking.

## Wire format

The endpoint is `POST https://api.typesafe.ai/v1/systemone` with `Authorization: Bearer <API_KEY>` and `Content-Type: application/json`. `systemone` is currently the API's only operation, so it's the only method on `IJevClient`.


Requests use snake_case, omit nulls, and put the `type` discriminator first. The documented example serializes byte for byte:

```json
{"model":"jev-latest","state":{"query":"Ken Suber","candidate":{"name":"Kenneth Suber","usbc":"1234-5678"}},"questions":{"same_person":{"type":"noul","instructions":"Is `candidate` the same person as `query`?"}}}
```

`JevJson.Options` exposes the serializer options. For why questions are polymorphic records rather than C# 15 `union` types, see [docs/union-decision.md](docs/union-decision.md).

## ASSUMPTIONS (still unconfirmed)

The request and response shapes match a real sample from the API (see `CaptainSampleTests`). These points remain open:

1. **Request id header** is read from `x-request-id`, then `request-id`. The sample didn't show response headers.
2. **Error body** format is unknown, so it's kept raw in `JevApiException.ResponseBody`.
3. **Question name rules** (`[A-Za-z0-9_-]{1,64}`) are a client-side choice and may be stricter than the API's.
4. **Choice criteria descriptions** may be empty strings (only null is rejected). Score criteria must be unique, which is a client-side choice.
5. **Score rounding** for `RoundedIndex`/`RoundedLabel` uses midpoints away from zero (1.5 gives 2). This is a convenience; `Score` is the raw weighted value.
6. **Probabilities** are assumed to always be present on choice and score answers. If they're missing, they default to an empty map.

## Building

```sh
dotnet build && dotnet test
dotnet pack src/Kaizen.Jev/Kaizen.Jev.csproj -c Release -o artifacts   # Kaizen.Jev.0.1.0.nupkg + .snupkg
```

The SDK is pinned in `global.json` (.NET 11 RC1). Package versions live in `Directory.Packages.props` (central package management), and shared properties in `Directory.Build.props`. SourceLink comes from the .NET SDK (no package needed). The build is deterministic, with `ContinuousIntegrationBuild` set on GitHub Actions.

If restore hangs on a sandboxed machine during package signature verification, set `NUGET_CERT_REVOCATION_MODE=offline`.

## Releasing (Trusted Publishing)

`.github/workflows/publish.yml` runs **only** when a tag matching `v*.*.*` is pushed. It builds, tests, packs with the version from the tag (`v0.1.0` gives `0.1.0`), exchanges a GitHub OIDC token for a short-lived nuget.org key via [`NuGet/login@v1`](https://github.com/NuGet/login), and pushes with `--skip-duplicate`. No long-lived API key secret is stored.

One-time setup (captain):

1. On nuget.org, open **Trusted Publishing** and add a GitHub Actions policy: owner `kaizen-hq`, repository `Kaizen.Jev`, workflow file `publish.yml`.
2. In the GitHub repo, go to Settings, then Secrets and variables, then Actions, then **Variables**, and add `NUGET_USER` set to the nuget.org account name that owns the policy. The workflow fails fast if it's empty.
3. Release: `git tag v0.1.0 && git push origin v0.1.0`.

`ci.yml` builds, tests and packs on every push to `main` and every PR.

## License

MIT © Kijana Woodard (the holder may change).

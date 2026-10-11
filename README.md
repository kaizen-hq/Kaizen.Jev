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
    // o.BaseAddress = new Uri("https://api.jev.ai/");         // default (see ASSUMPTIONS)
})
.AddStandardResilienceHandler();                               // optional: it returns IHttpClientBuilder

// inject IJevClient
var response = await jev.SystemOneAsync(new JevRequest
{
    // Model defaults to "jev-latest"
    State = JevState.FromObject(new { query = "Ken Suber", candidate = new { name = "Kenneth Suber", usbc = "1234-5678" } }),
    Questions = new Dictionary<string, JevQuestion>
    {
        ["same_person"] = new NoulQuestion("Is `candidate` the same person as `query`?"),
        ["sport"]       = new ChoiceQuestion("Which sport is this about?", ["bowling", "golf", "other"]),
        ["confidence"]  = new ScoreQuestion("How strong is the evidence?", ["none", "weak", "moderate", "strong"]),
    },
}, cancellationToken);

response.Model;                          // "jev-1.13.0" (the concrete model)
response.Results["same_person"].Noul;    // 0..1
response.Results["sport"].Choice;        // "bowling"
response.Results["sport"].Probabilities; // { "bowling": 0.8, ... }
response.Results["confidence"].Score;    // weighted score
response.Usage?.InputTokens;
response.RequestId;                      // from x-request-id / request-id header, if sent
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
- `choice`: 1..255 options, unique and non-empty
- `score`: 2..10 levels, unique and non-empty, lowest first

A non-2xx status or an unreadable body throws `JevApiException` with `StatusCode`, `ResponseBody` (capped at 64 KB) and `RequestId`. Cancellation surfaces as `OperationCanceledException`. If `ApiKey` is missing, DI options validation fails with `OptionsValidationException`; a hand-built client throws `InvalidOperationException`.

## Wire format

Requests use snake_case, omit nulls, and put the `type` discriminator first. The documented example serializes byte for byte:

```json
{"model":"jev-latest","state":{"query":"Ken Suber","candidate":{"name":"Kenneth Suber","usbc":"1234-5678"}},"questions":{"same_person":{"type":"noul","instructions":"Is `candidate` the same person as `query`?"}}}
```

`JevJson.Options` exposes the serializer options. For why questions are polymorphic records rather than C# 15 `union` types, see [docs/union-decision.md](docs/union-decision.md).

## ASSUMPTIONS (not specified by the API description; confirm against the real API)

1. **Base URL** defaults to `https://api.jev.ai/`. This is a placeholder, so set `BaseAddress` explicitly.
2. **Request field names** for choice options and score levels are `options` and `levels` (arrays of strings, levels ordered lowest to highest).
3. **Response envelope**: `{ "model": "...", "results": { "<question name>": {...} }, "usage": { "input_tokens": n } }`. The `results` key name is assumed.
4. **Result fields**: noul gives `noul` (number 0..1). Choice gives `choice` (the selected option string), `probabilities` and `confidence`. Score gives `score` (number), `probabilities` and `confidence`. An optional echoed `type` is read if present. All of these live on one flat `JevQuestionResult` with nullable fields, so unknown result shapes don't break parsing.
5. **`probabilities` is an object map** of label to probability (`{"bowling":0.8}`). For score it's keyed by level label. If the API returns an array in option or level order, parsing fails with `JevApiException` and the model needs changing.
6. **`score` scale** is unspecified (0-based index? 1-based? 0..1?). It's passed through as a raw number.
7. **Request id header** is read from `x-request-id`, then `request-id`.
8. **Error body** format is unknown, so it's kept raw in `JevApiException.ResponseBody`.
9. **Question name rules** (`[A-Za-z0-9_-]{1,64}`) are a client-side choice and may be stricter than the API's.

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

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
namespace Kaizen.Jev.Tests;

public class SerializationTests
{
    const string Example = """{"model":"jev-latest","state":{"query":"Ken Suber","candidate":{"name":"Kenneth Suber","usbc":"1234-5678"}},"questions":{"same_person":{"type":"noul","instructions":"Is `candidate` the same person as `query`?"}}}""";

    static JevRequest ExampleRequest() => new()
    {
        State = JevState.FromObject(new { query = "Ken Suber", candidate = new { name = "Kenneth Suber", usbc = "1234-5678" } }),
        Questions = new Dictionary<string, JevQuestion> { ["same_person"] = new NoulQuestion("Is `candidate` the same person as `query`?") },
    };

    [Fact]
    public void Request_matches_documented_example_exactly()
    {
        var json = JsonSerializer.Serialize(ExampleRequest(), JevJson.Options);
        Assert.Equal(JsonSerializer.Serialize(JsonDocument.Parse(Example).RootElement), JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement));
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(Example).RootElement, JsonDocument.Parse(json).RootElement));
    }

    [Fact]
    public void Example_round_trips()
    {
        var r = JsonSerializer.Deserialize<JevRequest>(Example, JevJson.Options)!;
        Assert.Equal("jev-latest", r.Model);
        Assert.Equal(JevStateKind.Object, r.State.Kind);
        Assert.IsType<NoulQuestion>(r.Questions["same_person"]);
        Assert.Equal(Example, JsonSerializer.Serialize(r, JevJson.Options));
    }

    [Fact]
    public void Choice_and_score_serialize_with_discriminator_first()
    {
        var q = new Dictionary<string, JevQuestion>
        {
            ["c"] = new ChoiceQuestion("pick", new Dictionary<string, string> { ["b"] = "bee", ["a"] = "ay" }),
            ["s"] = new ScoreQuestion("rate", ["low", "mid", "high"]),
        };
        var json = JsonSerializer.Serialize(q, JevJson.Options);
        Assert.Equal("""{"c":{"type":"choice","instructions":"pick","criteria":{"b":"bee","a":"ay"}},"s":{"type":"score","instructions":"rate","criteria":["low","mid","high"]}}""", json);
        var back = JsonSerializer.Deserialize<Dictionary<string, JevQuestion>>(json, JevJson.Options)!;
        Assert.Equal(["b", "a"], Assert.IsType<ChoiceQuestion>(back["c"]).Criteria.Keys);
        Assert.Equal(["low", "mid", "high"], Assert.IsType<ScoreQuestion>(back["s"]).Criteria);
    }

    [Fact]
    public void Unknown_question_type_fails_to_deserialize() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JevQuestion>("""{"type":"essay","instructions":"x"}""", JevJson.Options));

    [Theory]
    [InlineData("\"hello\"", JevStateKind.Text)]
    [InlineData("{\"a\":1}", JevStateKind.Object)]
    [InlineData("[\"a\",\"b\"]", JevStateKind.TextArray)]
    public void State_round_trips_each_shape(string json, JevStateKind kind)
    {
        var s = JsonSerializer.Deserialize<JevState>(json, JevJson.Options)!;
        Assert.Equal(kind, s.Kind);
        Assert.Equal(json, JsonSerializer.Serialize(s, JevJson.Options));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("[1,2]")]
    [InlineData("true")]
    public void State_rejects_other_shapes(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JevState>(json, JevJson.Options));

    [Fact]
    public void State_factories_and_implicit_conversions()
    {
        JevState t = "x"; JevState a = new[] { "a", "b" };
        Assert.Equal("x", t.Text);
        Assert.Equal(["a", "b"], a.Texts);
        Assert.Equal(JevState.FromJson("{\"k\":[1]}"), JevState.FromObject(new { k = new[] { 1 } }));
        Assert.Throws<ArgumentException>(() => JevState.FromJson("[1]"));
        Assert.Throws<InvalidOperationException>(() => t.Texts);
    }
}

public class ValidationTests
{
    static JevRequest Req(params (string, JevQuestion)[] qs) => new() { State = "s", Questions = qs.ToDictionary(x => x.Item1, x => x.Item2) };
    static IEnumerable<string> N(int n) => Enumerable.Range(0, n).Select(i => "o" + i);
    static Dictionary<string, string> C(params string[] labels) => labels.ToDictionary(l => l, l => "d " + l);
    static Dictionary<string, string> C(int n) => C(N(n).ToArray());

    [Fact] public void Needs_a_question() => Assert.Throws<JevValidationException>(() => Req().Validate());
    [Fact] public void Valid_limits_pass() => Req(("c1", new ChoiceQuestion("i", C("only"))), ("c255", new ChoiceQuestion("i", C(255))), ("s2", new ScoreQuestion("i", ["a", "b"])), ("s10", new ScoreQuestion("i", N(10).ToList()))).Validate();

    public static TheoryData<string, JevQuestion> Bad => new()
    {
        { "q", new NoulQuestion("") },
        { "q", new NoulQuestion("   ") },
        { "q", new ChoiceQuestion("i", C()) },
        { "q", new ChoiceQuestion("i", C(256)) },
        { "q", new ChoiceQuestion("i", new Dictionary<string, string> { ["a"] = null! }) },
        { "q", new ChoiceQuestion("i", C("a", " ")) },
        { "q", new ScoreQuestion("i", ["a"]) },
        { "q", new ScoreQuestion("i", N(11).ToList()) },
        { "q", new ScoreQuestion("i", ["a", "a"]) },
        { "", new NoulQuestion("i") },
        { "has space", new NoulQuestion("i") },
        { new string('x', 65), new NoulQuestion("i") },
    };

    [Theory, MemberData(nameof(Bad))]
    public void Bad_requests_throw(string name, JevQuestion q) => Assert.Throws<JevValidationException>(() => Req((name, q)).Validate());

    [Fact]
    public void Error_names_the_question()
    {
        var ex = Assert.Throws<JevValidationException>(() => Req(("tone", new ScoreQuestion("i", ["a"]))).Validate());
        Assert.Equal("tone", ex.QuestionName);
        Assert.Contains("2..10", ex.Message);
    }
}

public class ClientTests
{
    const string Response = """{"model": "jev-1.13.0", "answers": {"department": {"type": "choice", "choice": "technical", "confidence": 0.78, "probabilities": {"technical": 0.85, "sales": 0.0, "billing": 0.15}}, "frustration": {"type": "score", "score": 1.0, "confidence": 1.0, "legend": {"0": "Calm, just stating facts", "1": "Frustrated but civil", "2": "Very angry, strong language"}, "probabilities": {"0": 0.0, "1": 1.0, "2": 0.0}}, "is_urgent": {"type": "noul", "noul": 1.0}}, "usage": {"input_tokens": 392, "output_tokens": 65}}""";

    static JevClient Client(FakeHandler h, string key = "test-key") =>
        new(new HttpClient(h) { BaseAddress = new Uri("https://jev.test/") }, Options.Create(new JevClientOptions { ApiKey = key }));

    static JevRequest Req() => new() { State = "s", Questions = new Dictionary<string, JevQuestion> { ["q"] = new NoulQuestion("i") } };

    [Fact]
    public async Task Sends_post_with_bearer_and_json()
    {
        var h = FakeHandler.Json(Response);
        await Client(h).SystemOneAsync(Req(), CancellationToken.None);
        Assert.Equal(HttpMethod.Post, h.Request!.Method);
        Assert.Equal("https://jev.test/v1/systemone", h.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", h.Request.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", h.Request.Headers.Authorization.Parameter);
        Assert.Equal("application/json", h.Request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("""{"model":"jev-latest","state":"s","questions":{"q":{"type":"noul","instructions":"i"}}}""", h.RequestBody);
    }

    [Fact]
    public async Task Default_base_address_targets_typesafe_systemone()
    {
        var h = FakeHandler.Json(Response);
        var client = new JevClient(new HttpClient(h), Options.Create(new JevClientOptions { ApiKey = "test-key" }));
        await client.SystemOneAsync(Req(), CancellationToken.None);
        Assert.Equal(new Uri("https://api.typesafe.ai/"), JevClientOptions.DefaultBaseAddress);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", h.Request!.RequestUri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, h.Request.Method);
        Assert.Equal("Bearer test-key", h.Request.Headers.Authorization!.ToString());
        Assert.Equal("application/json", h.Request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Parses_noul_choice_score_usage_and_model()
    {
        var r = await Client(FakeHandler.Json(Response, requestId: "req_1")).SystemOneAsync(Req(), CancellationToken.None);
        Assert.Equal("jev-1.13.0", r.Model);
        Assert.NotNull(r.Usage);
        Assert.Equal("req_1", r.RequestId);
        Assert.Equal(392, r.Usage.InputTokens);
        Assert.Equal(65, r.Usage.OutputTokens);
        Assert.Equal(3, r.Answers.Count);

        var d = r.GetAnswer<ChoiceAnswer>("department");
        Assert.Equal("choice", d.Type);
        Assert.Equal("technical", d.Choice);
        Assert.Equal(0.78, d.Confidence);
        Assert.Equal(new Dictionary<string, double> { ["technical"] = 0.85, ["sales"] = 0.0, ["billing"] = 0.15 }, d.Probabilities);

        var f = r.GetAnswer<ScoreAnswer>("frustration");
        Assert.Equal(1.0, f.Score);
        Assert.Equal(1.0, f.Confidence);
        Assert.Equal("Very angry, strong language", f.Legend["2"]);
        Assert.Equal(1, f.RoundedIndex);
        Assert.Equal("Frustrated but civil", f.RoundedLabel);
        Assert.Equal("Calm, just stating facts", f.GetLabel(0));
        Assert.Null(f.GetLabel(3));
        Assert.Equal(1.0, f.GetProbability(1));
        Assert.Equal(0.0, f.GetProbability(2));

        Assert.True(r.TryGetAnswer<NoulAnswer>("is_urgent", out var u));
        Assert.Equal(1.0, u.Noul);
        Assert.False(r.TryGetAnswer<ChoiceAnswer>("is_urgent", out _));
        Assert.False(r.TryGetAnswer<NoulAnswer>("missing", out _));
        Assert.Throws<KeyNotFoundException>(() => r.GetAnswer<NoulAnswer>("missing"));
        Assert.Throws<InvalidCastException>(() => r.GetAnswer<ScoreAnswer>("is_urgent"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Non_success_maps_to_JevApiException(HttpStatusCode code)
    {
        var ex = await Assert.ThrowsAsync<JevApiException>(() => Client(FakeHandler.Json("""{"error":"nope"}""", code, "req_9")).SystemOneAsync(Req(), CancellationToken.None));
        Assert.Equal(code, ex.StatusCode);
        Assert.Equal("""{"error":"nope"}""", ex.ResponseBody);
        Assert.Equal("req_9", ex.RequestId);
        Assert.Contains(((int)code).ToString(), ex.Message);
    }

    [Fact]
    public async Task Unreadable_body_maps_to_JevApiException()
    {
        var ex = await Assert.ThrowsAsync<JevApiException>(() => Client(FakeHandler.Json("not json")).SystemOneAsync(Req(), CancellationToken.None));
        Assert.IsType<JsonException>(ex.InnerException);
    }

    [Fact]
    public async Task Validation_fails_before_sending()
    {
        var h = FakeHandler.Json(Response);
        var bad = new JevRequest { State = "s", Questions = new Dictionary<string, JevQuestion>() };
        await Assert.ThrowsAsync<JevValidationException>(() => Client(h).SystemOneAsync(bad, CancellationToken.None));
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task Missing_api_key_throws_without_sending()
    {
        var h = FakeHandler.Json(Response);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(h, "").SystemOneAsync(Req(), CancellationToken.None));
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task Cancellation_is_honored()
    {
        var h = new FakeHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(h).SystemOneAsync(Req(), cts.Token));
    }

    [Fact]
    public async Task Already_cancelled_token_throws()
    {
        var h = FakeHandler.Json(Response);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(h).SystemOneAsync(Req(), new CancellationToken(true)));
    }
}

public class DiTests
{
    [Fact]
    public async Task AddJevClient_registers_typed_client_with_options()
    {
        var h = FakeHandler.Json("""{"model":"jev-1.13.0","answers":{},"usage":{"input_tokens":1,"output_tokens":0}}""");
        var services = new ServiceCollection();
        IHttpClientBuilder b = services.AddJevClient(o => { o.ApiKey = "k"; o.BaseAddress = new Uri("https://example.test/base"); });
        b.ConfigurePrimaryHttpMessageHandler(() => h);
        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IJevClient>();
        Assert.IsType<JevClient>(client);
        var r = await client.SystemOneAsync(new JevRequest { State = "s", Questions = new Dictionary<string, JevQuestion> { ["q"] = new NoulQuestion("i") } }, CancellationToken.None);
        Assert.Equal("https://example.test/base/v1/systemone", h.Request!.RequestUri!.ToString());
        Assert.Equal("jev-1.13.0", r.Model);
    }

    [Fact]
    public void Missing_api_key_fails_options_validation()
    {
        var services = new ServiceCollection();
        services.AddJevClient(_ => { });
        using var sp = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IJevClient>());
    }
}

public class CaptainSampleTests
{
    const string SampleRequest = """{"state": "Hi, I've been trying to connect my Stripe account for 3 days and the integration keeps failing. I'm losing sales. Please help ASAP.", "model": "jev-latest", "questions": {"department": {"type": "choice", "instructions": "Which team should handle this", "criteria": {"billing": "Payment or subscription issues", "technical": "Bugs or integration problems", "sales": "Pricing or account questions"}}, "frustration": {"type": "score", "instructions": "How frustrated the customer appears", "criteria": ["Calm, just stating facts", "Frustrated but civil", "Very angry, strong language"]}, "is_urgent": {"type": "noul", "instructions": "The message conveys urgency or time-sensitivity"}}}""";

    internal static JevRequest Build() => new()
    {
        State = "Hi, I've been trying to connect my Stripe account for 3 days and the integration keeps failing. I'm losing sales. Please help ASAP.",
        Questions = new Dictionary<string, JevQuestion>
        {
            ["department"] = new ChoiceQuestion("Which team should handle this", new Dictionary<string, string>
            {
                ["billing"] = "Payment or subscription issues",
                ["technical"] = "Bugs or integration problems",
                ["sales"] = "Pricing or account questions",
            }),
            ["frustration"] = new ScoreQuestion("How frustrated the customer appears",
                ["Calm, just stating facts", "Frustrated but civil", "Very angry, strong language"]),
            ["is_urgent"] = new NoulQuestion("The message conveys urgency or time-sensitivity"),
        },
    };

    [Fact]
    public void Sample_request_serializes_equal()
    {
        var json = JsonSerializer.Serialize(Build(), JevJson.Options);
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(SampleRequest).RootElement, JsonDocument.Parse(json).RootElement), json);
        // Criteria order is preserved (billing, technical, sales).
        Assert.Contains("""{"billing":"Payment or subscription issues","technical":"Bugs or integration problems","sales":"Pricing or account questions"}""", json);
        // Byte-for-byte modulo whitespace and the model/state key order the captain wrote.
        Assert.Equal(JsonSerializer.Serialize(JsonSerializer.Deserialize<JevRequest>(SampleRequest, JevJson.Options), JevJson.Options), json);
        Build().Validate();
    }

    [Fact]
    public void Response_round_trips_with_type_first()
    {
        var json = """{"model":"jev-1.13.0","answers":{"a":{"type":"noul","noul":0.5},"b":{"type":"choice","choice":"x","confidence":0.1,"probabilities":{"x":1}}},"usage":{"input_tokens":1,"output_tokens":2}}""";
        var r = JsonSerializer.Deserialize<JevResponse>(json, JevJson.Options)!;
        Assert.Equal(json, JsonSerializer.Serialize(r, JevJson.Options));
    }

    [Fact]
    public void Unknown_answer_type_is_tolerated_and_raw_json_kept()
    {
        var r = JsonSerializer.Deserialize<JevResponse>("""{"model":"m","answers":{"x":{"type":"rank","order":["a","b"]}}}""", JevJson.Options)!;
        var u = r.GetAnswer<UnknownAnswer>("x");
        Assert.Equal("rank", u.Type);
        Assert.Equal("b", u.Raw.GetProperty("order")[1].GetString());
        Assert.Equal("""{"type":"rank","order":["a","b"]}""", JsonSerializer.Serialize<JevAnswer>(u, JevJson.Options));
    }

    [Theory]
    [InlineData(0.49, 0)]
    [InlineData(0.5, 1)]
    [InlineData(1.6, 2)]
    public void Score_rounds_to_nearest_index(double score, int index) =>
        Assert.Equal(index, new ScoreAnswer { Score = score }.RoundedIndex);
}

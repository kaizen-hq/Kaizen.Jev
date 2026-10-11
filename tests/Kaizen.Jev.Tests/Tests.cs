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
            ["c"] = new ChoiceQuestion("pick", ["a", "b"]),
            ["s"] = new ScoreQuestion("rate", ["low", "mid", "high"]),
        };
        var json = JsonSerializer.Serialize(q, JevJson.Options);
        Assert.Equal("""{"c":{"type":"choice","instructions":"pick","options":["a","b"]},"s":{"type":"score","instructions":"rate","levels":["low","mid","high"]}}""", json);
        var back = JsonSerializer.Deserialize<Dictionary<string, JevQuestion>>(json, JevJson.Options)!;
        Assert.Equal(["a", "b"], Assert.IsType<ChoiceQuestion>(back["c"]).Options);
        Assert.Equal(3, Assert.IsType<ScoreQuestion>(back["s"]).Levels.Count);
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

    [Fact] public void Needs_a_question() => Assert.Throws<JevValidationException>(() => Req().Validate());
    [Fact] public void Valid_limits_pass() => Req(("c1", new ChoiceQuestion("i", ["only"])), ("c255", new ChoiceQuestion("i", N(255).ToList())), ("s2", new ScoreQuestion("i", ["a", "b"])), ("s10", new ScoreQuestion("i", N(10).ToList()))).Validate();

    public static TheoryData<string, JevQuestion> Bad => new()
    {
        { "q", new NoulQuestion("") },
        { "q", new NoulQuestion("   ") },
        { "q", new ChoiceQuestion("i", []) },
        { "q", new ChoiceQuestion("i", N(256).ToList()) },
        { "q", new ChoiceQuestion("i", ["a", "a"]) },
        { "q", new ChoiceQuestion("i", ["a", " "]) },
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
    const string Response = """
    {"model":"jev-1.13.0",
     "results":{
       "same_person":{"noul":0.93},
       "sport":{"choice":"bowling","probabilities":{"bowling":0.8,"golf":0.2},"confidence":0.7},
       "skill":{"score":3.4,"probabilities":{"1":0.1,"2":0.1,"3":0.2,"4":0.6},"confidence":0.55}},
     "usage":{"input_tokens":123}}
    """;

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
        Assert.Equal(123, r.Usage!.InputTokens);
        Assert.Equal("req_1", r.RequestId);
        Assert.Equal(0.93, r.Results["same_person"].Noul);
        Assert.Equal("bowling", r.Results["sport"].Choice);
        Assert.Equal(0.8, r.Results["sport"].Probabilities!["bowling"]);
        Assert.Equal(0.7, r.Results["sport"].Confidence);
        Assert.Equal(3.4, r.Results["skill"].Score);
        Assert.Equal(4, r.Results["skill"].Probabilities!.Count);
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
        var h = FakeHandler.Json("""{"model":"jev-1.13.0","results":{},"usage":{"input_tokens":1}}""");
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

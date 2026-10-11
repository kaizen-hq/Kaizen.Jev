using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
namespace Kaizen.Jev.Tests;

// Examples taken from https://docs.typesafe.ai/api (fetched 2026-10-11).
public class DocsRequestTests
{
    static string Ser(JevRequest r) => JsonSerializer.Serialize(r, JevJson.Options);
    static void AssertJson(string expected, string actual) =>
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(expected).RootElement, JsonDocument.Parse(actual).RootElement), actual);

    [Fact]
    public void Noul_with_true_false_criteria()
    {
        var r = new JevRequest
        {
            State = "Help! My payouts have been failing for 3 days.",
            Questions = new Dictionary<string, JevQuestion>
            {
                ["is_urgent"] = new NoulQuestion("Does this convey urgency?")
                {
                    Criteria = new NoulCriteria { True = "Explicitly time-sensitive", False = "No urgency expressed" },
                },
            },
        };
        Assert.Equal("""{"state":"Help! My payouts have been failing for 3 days.","model":"jev-latest","questions":{"is_urgent":{"type":"noul","instructions":"Does this convey urgency?","criteria":{"true":"Explicitly time-sensitive","false":"No urgency expressed"}}}}""", Ser(r));
        r.Validate();
    }

    [Fact]
    public void Structured_instructions_object()
    {
        var q = new NoulQuestion(JevContent.FromObject(new
        {
            potential_duplicate = new { name = "John Smith", location = "Oakland, California", last_employer = "Google" },
            question = "Is the resume for the same person as `potential_duplicate`?",
        }));
        var json = JsonSerializer.Serialize<JevQuestion>(q, JevJson.Options);
        AssertJson("""{"type":"noul","instructions":{"potential_duplicate":{"name":"John Smith","location":"Oakland, California","last_employer":"Google"},"question":"Is the resume for the same person as `potential_duplicate`?"}}""", json);
        Assert.Equal(q, JsonSerializer.Deserialize<JevQuestion>(json, JevJson.Options));
    }

    [Fact]
    public void Choice_criteria_allow_null_and_structured_descriptions()
    {
        var q = new ChoiceQuestion("Which team?", new Dictionary<string, JevContent?>
        {
            ["billing"] = "Payments, invoicing, refunds",
            ["technical"] = null,
            ["sales"] = JevContent.FromJson("""{"covers":["pricing","upgrades"]}"""),
        });
        var json = JsonSerializer.Serialize<JevQuestion>(q, JevJson.Options);
        Assert.Equal("""{"type":"choice","instructions":"Which team?","criteria":{"billing":"Payments, invoicing, refunds","technical":null,"sales":{"covers":["pricing","upgrades"]}}}""", json);
        var back = Assert.IsType<ChoiceQuestion>(JsonSerializer.Deserialize<JevQuestion>(json, JevJson.Options));
        Assert.Null(back.Criteria["technical"]);
        Assert.Equal(["billing", "technical", "sales"], back.Criteria.Keys);
    }

    [Fact]
    public void Score_criteria_may_be_structured_and_repeat()
    {
        var q = new ScoreQuestion("How frustrated is the customer?", ["Calm", JevContent.FromJson("""["Frustrated","civil"]"""), "Calm"]);
        Assert.Equal("""{"type":"score","instructions":"How frustrated is the customer?","criteria":["Calm",["Frustrated","civil"],"Calm"]}""", JsonSerializer.Serialize<JevQuestion>(q, JevJson.Options));
        q.Validate("q");
    }

    [Fact]
    public void Any_question_id_is_allowed()
    {
        new JevRequest { State = "s", Questions = new Dictionary<string, JevQuestion> { ["has space / ünïcode"] = new NoulQuestion("i") } }.Validate();
    }

    [Fact]
    public void Content_rejects_scalars()
    {
        Assert.Throws<ArgumentException>(() => JevContent.FromJson("42"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JevQuestion>("""{"type":"noul","instructions":true}""", JevJson.Options));
    }

    [Fact]
    public void Model_constants()
    {
        Assert.Equal("jev-latest", JevModels.Latest);
        Assert.Equal("jev-preview", JevModels.Preview);
        Assert.Equal("jev-1.13.0", JevModels.Jev_1_13_0);
    }

    [Theory]
    [InlineData("""{"model":"jev-1.13.0","answers":{"is_urgent":{"type":"noul","noul":0.95}},"usage":{"input_tokens":296,"output_tokens":20}}""")]
    [InlineData("""{"model":"jev-1.13.0","answers":{"department":{"type":"choice","choice":"billing","probabilities":{"billing":0.88,"technical":0.12,"sales":0.0},"confidence":0.81}},"usage":{"input_tokens":318,"output_tokens":34}}""")]
    [InlineData("""{"model":"jev-1.13.0","answers":{"frustration":{"type":"score","score":1.05,"legend":{"0":"Calm","1":"Frustrated","2":"Very angry"},"probabilities":{"0":0.0,"1":0.95,"2":0.05},"confidence":0.92}},"usage":{"input_tokens":304,"output_tokens":18}}""")]
    public void Docs_response_examples_parse(string json)
    {
        var r = JsonSerializer.Deserialize<JevResponse>(json, JevJson.Options)!;
        Assert.Equal("jev-1.13.0", r.Model);
        Assert.True(r.Usage!.InputTokens > 0);
        var a = Assert.Single(r.Answers).Value;
        Assert.IsNotType<UnknownAnswer>(a);
        if (a is ScoreAnswer s) { Assert.Equal(1, s.RoundedIndex); Assert.Equal("Frustrated", s.RoundedLabel); Assert.Equal(0.95, s.GetProbability(1)); }
        if (a is ChoiceAnswer c) Assert.Equal(0.81, c.Confidence);
        if (a is NoulAnswer n) Assert.Equal(0.95, n.Noul);
    }
}

public class DocsErrorAndRetryTests
{
    static JevRequest Req() => new() { State = "s", Questions = new Dictionary<string, JevQuestion> { ["q"] = new NoulQuestion("i") } };
    const string Ok = """{"model":"jev-1.13.0","answers":{"q":{"type":"noul","noul":1.0}},"usage":{"input_tokens":1,"output_tokens":1}}""";

    static JevClient Client(FakeHandler h, int retries = 2) => new(new HttpClient(h) { BaseAddress = new Uri("https://api.typesafe.ai/") },
        Options.Create(new JevClientOptions { ApiKey = "k", MaxRetries = retries, RetryBaseDelay = TimeSpan.FromMilliseconds(1) }));

    static HttpResponseMessage Resp(int code, string body, string? retryAfter = null)
    {
        var r = new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter is not null) r.Headers.TryAddWithoutValidation("retry-after", retryAfter);
        return r;
    }

    static FakeHandler Sequence(params HttpResponseMessage[] responses)
    {
        var q = new Queue<HttpResponseMessage>(responses);
        return new FakeHandler((_, _) => Task.FromResult(q.Dequeue()));
    }

    [Fact]
    public async Task Retries_429_then_succeeds_honoring_retry_after()
    {
        var h = Sequence(Resp(429, """{"error":"rate limited"}""", "0"), Resp(200, Ok));
        var r = await Client(h).SystemOneAsync(Req(), CancellationToken.None);
        Assert.Equal(2, h.Calls);
        Assert.Equal(1.0, r.GetAnswer<NoulAnswer>("q").Noul);
    }

    [Fact]
    public async Task Retries_529_until_exhausted()
    {
        var h = Sequence(Resp(529, "{}"), Resp(529, "{}"), Resp(529, """{"error":{"type":"overloaded","message":"TypeSafe is temporarily overloaded"}}""", "1"));
        var ex = await Assert.ThrowsAsync<JevApiException>(() => Client(h).SystemOneAsync(Req(), CancellationToken.None));
        Assert.Equal(3, h.Calls);
        Assert.Equal(3, ex.Attempts);
        Assert.True(ex.IsOverloaded);
        Assert.True(ex.IsRetryable);
        Assert.Equal(529, (int)ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), ex.RetryAfter);
        Assert.Equal("overloaded", ex.ErrorCode);
        Assert.Equal("TypeSafe is temporarily overloaded", ex.ErrorMessage);
        Assert.Contains("529 Overloaded", ex.Message);
        Assert.Contains("after 3 attempts", ex.Message);
    }

    [Fact]
    public async Task Retries_can_be_disabled()
    {
        var h = Sequence(Resp(429, "{}"));
        var ex = await Assert.ThrowsAsync<JevApiException>(() => Client(h, retries: 0).SystemOneAsync(Req(), CancellationToken.None));
        Assert.Equal(1, h.Calls);
        Assert.True(ex.IsRateLimited);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(422)]
    [InlineData(500)]
    public async Task Non_retryable_statuses_fail_immediately(int code)
    {
        var h = Sequence(Resp(code, "{}"));
        var ex = await Assert.ThrowsAsync<JevApiException>(() => Client(h).SystemOneAsync(Req(), CancellationToken.None));
        Assert.Equal(1, h.Calls);
        Assert.False(ex.IsRetryable);
        Assert.Equal(code == 401, ex.IsUnauthorized);
        Assert.Equal(code == 422, ex.IsValidationError);
    }

    [Fact]
    public async Task Validation_422_detail_is_mapped_and_raw_body_kept()
    {
        const string body = """{"detail":[{"loc":["body","questions","q","criteria"],"msg":"Field required","type":"missing"}]}""";
        var ex = await Assert.ThrowsAsync<JevApiException>(() => Client(Sequence(Resp(422, body))).SystemOneAsync(Req(), CancellationToken.None));
        Assert.Equal(body, ex.ResponseBody);
        Assert.Equal("body.questions.q.criteria: Field required", ex.ErrorMessage);
        Assert.Equal(JsonValueKind.Array, ex.ErrorDetails!.Value.ValueKind);
        Assert.Contains("Field required", ex.Message);
    }

    [Theory]
    [InlineData("""{"error":"Invalid API key"}""", null, "Invalid API key")]
    [InlineData("""{"error":{"code":"invalid_api_key","message":"Invalid API key"}}""", "invalid_api_key", "Invalid API key")]
    [InlineData("""{"message":"Invalid API key","code":"unauthorized"}""", "unauthorized", "Invalid API key")]
    [InlineData("""{"detail":"Not authenticated"}""", null, "Not authenticated")]
    [InlineData("not json", null, null)]
    public void Error_body_shapes(string body, string? code, string? message)
    {
        var ex = new JevApiException("x", HttpStatusCode.Unauthorized, body, null);
        Assert.Equal(code, ex.ErrorCode);
        Assert.Equal(message, ex.ErrorMessage);
        Assert.Equal(body, ex.ResponseBody);
    }

    [Fact]
    public async Task Cancellation_during_backoff()
    {
        var h = Sequence(Resp(429, "{}", "10"), Resp(200, Ok));
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(h).SystemOneAsync(Req(), cts.Token));
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task List_models()
    {
        var h = FakeHandler.Json("""{"models":[{"name":"jev-latest","description":"Flagship","release_date":"2026-09-01"},{"name":"jev-preview","description":"Preview","release_date":"2026-09-01"}]}""");
        var m = await Client(h).ListModelsAsync(CancellationToken.None);
        Assert.Equal(HttpMethod.Get, h.Request!.Method);
        Assert.Equal("https://api.typesafe.ai/v1/models", h.Request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer k", h.Request.Headers.Authorization!.ToString());
        Assert.Equal(["jev-latest", "jev-preview"], m.Models.Select(x => x.Name));
        Assert.Equal("2026-09-01", m.Models[0].ReleaseDate);
    }
}

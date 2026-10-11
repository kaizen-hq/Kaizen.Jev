using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Kaizen.Jev;

/// <summary>Options for <see cref="JevClient"/>.</summary>
public sealed class JevClientOptions
{
    /// <summary>The API base address.</summary>
    public static readonly Uri DefaultBaseAddress = new("https://api.typesafe.ai/");

    /// <summary>API key sent as <c>Authorization: Bearer</c>. Required.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>API base address. Defaults to <see cref="DefaultBaseAddress"/>.</summary>
    public Uri BaseAddress { get; set; } = DefaultBaseAddress;

    /// <summary>Retries after a 429 or 529 response (exponential backoff with jitter, honoring <c>retry-after</c>). Default 2; 0 disables.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>First backoff delay; doubles each retry. Default 500 ms.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound for any single wait, including a server <c>retry-after</c>. Default 30 s.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Client for the Jev API (https://docs.typesafe.ai/api).</summary>
public interface IJevClient
{
    /// <summary>Validates and sends a request to <c>POST /v1/systemone</c>.</summary>
    Task<JevResponse> SystemOneAsync(JevRequest request, CancellationToken cancellationToken = default);

    /// <summary>Lists the model names your account can send (<c>GET /v1/models</c>).</summary>
    Task<JevModelList> ListModelsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Typed-HttpClient implementation of <see cref="IJevClient"/>.</summary>
public sealed class JevClient : IJevClient
{
    private const int MaxBodyChars = 64 * 1024;
    private readonly HttpClient _http;
    private readonly JevClientOptions _options;

    /// <summary>Creates a client. Usually resolved through <see cref="JevServiceCollectionExtensions.AddJevClient"/>.</summary>
    public JevClient(HttpClient http, IOptions<JevClientOptions> options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<JevResponse> SystemOneAsync(JevRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var (body, requestId, status) = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, Url("v1/systemone")) { Content = JsonContent.Create(request, options: JevJson.Options) },
            cancellationToken).ConfigureAwait(false);
        return Parse<JevResponse>(body, requestId, status) with { RequestId = requestId };
    }

    /// <inheritdoc />
    public async Task<JevModelList> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var (body, requestId, status) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Url("v1/models")), cancellationToken).ConfigureAwait(false);
        return Parse<JevModelList>(body, requestId, status);
    }

    private Uri Url(string path) => new(_http.BaseAddress ?? _options.BaseAddress, path);

    private async Task<(string Body, string? RequestId, HttpStatusCode Status)> SendAsync(Func<HttpRequestMessage> create, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("JevClientOptions.ApiKey is not configured.");

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var message = create();
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var requestId = GetRequestId(response);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return (body, requestId, response.StatusCode);

            var retryAfter = GetRetryAfter(response);
            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode == 529;
            if (retryable && attempt <= _options.MaxRetries)
            {
                await Task.Delay(Backoff(attempt, retryAfter), ct).ConfigureAwait(false);
                continue;
            }

            var errorMessage = JevApiException.ParseError(body).Message;
            throw new JevApiException(
                $"Jev API returned {(int)response.StatusCode} {Reason(response)}{(requestId is null ? "" : $" (request id {requestId})")}"
                + $"{(attempt > 1 ? $" after {attempt} attempts" : "")}: {errorMessage ?? Trim(body, 500)}",
                response.StatusCode, Trim(body, MaxBodyChars), requestId, retryAfter: retryAfter, attempts: attempt);
        }
    }

    private TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        var delay = retryAfter ?? TimeSpan.FromTicks((long)(_options.RetryBaseDelay.Ticks * Math.Pow(2, attempt - 1) * (0.8 + Random.Shared.NextDouble() * 0.4)));
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        return delay > _options.MaxRetryDelay ? _options.MaxRetryDelay : delay;
    }

    private static T Parse<T>(string body, string? requestId, HttpStatusCode status) where T : class
    {
        T? parsed;
        try { parsed = JsonSerializer.Deserialize<T>(body, JevJson.Options); }
        catch (JsonException ex)
        {
            throw new JevApiException($"Jev API returned an unreadable response body: {ex.Message}", status, Trim(body, MaxBodyChars), requestId, ex);
        }
        return parsed ?? throw new JevApiException("Jev API returned an empty response body.", status, body, requestId);
    }

    private static string Reason(HttpResponseMessage r) =>
        r.ReasonPhrase ?? ((int)r.StatusCode == 529 ? "Overloaded" : r.StatusCode.ToString());

    private static TimeSpan? GetRetryAfter(HttpResponseMessage r)
    {
        var h = r.Headers.RetryAfter;
        if (h?.Delta is { } d) return d;
        if (h?.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    private static string? GetRequestId(HttpResponseMessage r)
    {
        foreach (var h in new[] { "x-request-id", "request-id" })
            if (r.Headers.TryGetValues(h, out var v)) return v.FirstOrDefault();
        return null;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

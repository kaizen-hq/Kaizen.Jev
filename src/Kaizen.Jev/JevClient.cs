using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Kaizen.Jev;

/// <summary>Options for <see cref="JevClient"/>.</summary>
public sealed class JevClientOptions
{
    /// <summary>The default API base address.</summary>
    public static readonly Uri DefaultBaseAddress = new("https://api.jev.ai/");

    /// <summary>API key sent as <c>Authorization: Bearer</c>. Required.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>API base address. Defaults to <see cref="DefaultBaseAddress"/> (assumed; confirm).</summary>
    public Uri BaseAddress { get; set; } = DefaultBaseAddress;
}

/// <summary>Client for the Jev API.</summary>
public interface IJevClient
{
    /// <summary>Validates and sends a request to <c>POST /v1/systemone</c>.</summary>
    Task<JevResponse> SystemOneAsync(JevRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Typed-HttpClient implementation of <see cref="IJevClient"/>.</summary>
public sealed class JevClient : IJevClient
{
    private const string Path = "v1/systemone";
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
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("JevClientOptions.ApiKey is not configured.");

        var baseAddress = _http.BaseAddress ?? _options.BaseAddress;
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, Path))
        {
            Content = JsonContent.Create(request, options: JevJson.Options),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var requestId = GetRequestId(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new JevApiException(
                $"Jev API returned {(int)response.StatusCode} {response.ReasonPhrase}{(requestId is null ? "" : $" (request id {requestId})")}: {Trim(body, 500)}",
                response.StatusCode, Trim(body, MaxBodyChars), requestId);

        JevResponse? parsed;
        try { parsed = JsonSerializer.Deserialize<JevResponse>(body, JevJson.Options); }
        catch (JsonException ex)
        {
            throw new JevApiException($"Jev API returned an unreadable response body: {ex.Message}", response.StatusCode, Trim(body, MaxBodyChars), requestId, ex);
        }
        if (parsed is null)
            throw new JevApiException("Jev API returned an empty response body.", response.StatusCode, body, requestId);
        return parsed with { RequestId = requestId };
    }

    private static string? GetRequestId(HttpResponseMessage r)
    {
        foreach (var h in new[] { "x-request-id", "request-id" })
            if (r.Headers.TryGetValues(h, out var v)) return v.FirstOrDefault();
        return null;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

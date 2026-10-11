using System.Net;
using System.Text.Json;

namespace Kaizen.Jev;

/// <summary>The request broke a documented limit; nothing was sent.</summary>
public sealed class JevValidationException(string message, string? questionName = null) : ArgumentException(message)
{
    /// <summary>The offending question id, if the error is about one question.</summary>
    public string? QuestionName { get; } = questionName;
}

/// <summary>
/// Jev returned a non-success status (documented: 401, 422, 429, 529) or an unreadable body.
/// The raw body is always kept; <see cref="ErrorMessage"/>, <see cref="ErrorCode"/> and <see cref="ErrorDetails"/>
/// are extracted from the JSON error body when present.
/// </summary>
public sealed class JevApiException : Exception
{
    /// <summary>Creates the exception.</summary>
    public JevApiException(string message, HttpStatusCode statusCode, string? responseBody, string? requestId,
        Exception? inner = null, TimeSpan? retryAfter = null, int attempts = 1)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        RequestId = requestId;
        RetryAfter = retryAfter;
        Attempts = attempts;
        (ErrorCode, ErrorMessage, ErrorDetails) = ParseError(responseBody);
    }

    /// <summary>HTTP status code.</summary>
    public HttpStatusCode StatusCode { get; }
    /// <summary>Raw response body (may be truncated to 64 KB).</summary>
    public string? ResponseBody { get; }
    /// <summary>Request id header from the response, if any.</summary>
    public string? RequestId { get; }
    /// <summary>The <c>retry-after</c> delay from the last response, if it carried one.</summary>
    public TimeSpan? RetryAfter { get; }
    /// <summary>How many attempts were made (1 + retries).</summary>
    public int Attempts { get; }
    /// <summary>Error code/type from the body (<c>error.code</c>, <c>error.type</c>, <c>code</c> or <c>type</c>), if any.</summary>
    public string? ErrorCode { get; }
    /// <summary>Human-readable message from the body, if any.</summary>
    public string? ErrorMessage { get; }
    /// <summary>Structured detail from the body (e.g. the offending field on 422), if any.</summary>
    public JsonElement? ErrorDetails { get; }

    /// <summary>401: missing or invalid API key.</summary>
    public bool IsUnauthorized => StatusCode == HttpStatusCode.Unauthorized;
    /// <summary>422: the request body failed validation.</summary>
    public bool IsValidationError => StatusCode == HttpStatusCode.UnprocessableEntity;
    /// <summary>429: rate limit exceeded.</summary>
    public bool IsRateLimited => StatusCode == HttpStatusCode.TooManyRequests;
    /// <summary>529: TypeSafe is temporarily overloaded.</summary>
    public bool IsOverloaded => (int)StatusCode == 529;
    /// <summary>True for statuses the docs say to retry with backoff (429, 529).</summary>
    public bool IsRetryable => IsRateLimited || IsOverloaded;

    internal static (string? Code, string? Message, JsonElement? Details) ParseError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return default;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;
            string? code = Str(root, "code") ?? Str(root, "type");
            string? message = Str(root, "message");
            JsonElement? details = null;
            if (root.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) message ??= err.GetString();
                else if (err.ValueKind == JsonValueKind.Object)
                {
                    code = Str(err, "code") ?? Str(err, "type") ?? code;
                    message = Str(err, "message") ?? message;
                    if (err.TryGetProperty("details", out var d) || err.TryGetProperty("detail", out d)) details = d.Clone();
                }
            }
            if (root.TryGetProperty("detail", out var detail) || root.TryGetProperty("details", out detail))
            {
                if (detail.ValueKind == JsonValueKind.String) message ??= detail.GetString();
                else
                {
                    details ??= detail.Clone();
                    message ??= DescribeDetail(detail);
                }
            }
            return (code, message, details);
        }
        catch (JsonException) { return default; }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // FastAPI/pydantic-style: [{"loc":["body","questions","q","criteria"],"msg":"...","type":"..."}]
    private static string? DescribeDetail(JsonElement detail)
    {
        var first = detail.ValueKind == JsonValueKind.Array && detail.GetArrayLength() > 0 ? detail[0] : detail;
        if (first.ValueKind != JsonValueKind.Object) return null;
        var msg = Str(first, "msg") ?? Str(first, "message");
        if (msg is null) return null;
        if (first.TryGetProperty("loc", out var loc) && loc.ValueKind == JsonValueKind.Array)
            msg = $"{string.Join(".", loc.EnumerateArray().Select(x => x.ToString()))}: {msg}";
        return msg;
    }
}

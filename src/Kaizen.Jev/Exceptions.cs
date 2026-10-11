using System.Net;

namespace Kaizen.Jev;

/// <summary>The request broke a documented client-side limit; nothing was sent.</summary>
public sealed class JevValidationException(string message, string? questionName = null) : ArgumentException(message)
{
    /// <summary>The offending question name, if the error is about one question.</summary>
    public string? QuestionName { get; } = questionName;
}

/// <summary>Jev returned a non-success status or an unreadable body.</summary>
public sealed class JevApiException(string message, HttpStatusCode statusCode, string? responseBody, string? requestId, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>HTTP status code.</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;
    /// <summary>Raw response body (may be truncated to 64 KB).</summary>
    public string? ResponseBody { get; } = responseBody;
    /// <summary>Request id header from the response, if any.</summary>
    public string? RequestId { get; } = requestId;
}

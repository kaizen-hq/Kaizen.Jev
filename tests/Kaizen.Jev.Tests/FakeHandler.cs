using System.Net;
using System.Text;
namespace Kaizen.Jev.Tests;

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public HttpRequestMessage? Request;
    public string? RequestBody;
    public int Calls;
    public static FakeHandler Json(string json, HttpStatusCode code = HttpStatusCode.OK, string? requestId = null) =>
        new((_, _) =>
        {
            var r = new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (requestId is not null) r.Headers.Add("x-request-id", requestId);
            return Task.FromResult(r);
        });
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        Request = request;
        RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        return await respond(request, ct);
    }
}

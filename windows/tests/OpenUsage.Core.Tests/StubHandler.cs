using System.Net;
using System.Text;

namespace OpenUsage.Core.Tests;

/// Returns canned responses in order and records each request.
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses;

    public StubHandler(params (HttpStatusCode, string)[] responses) => _responses = new(responses);

    public List<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var (status, body) = _responses.Dequeue();
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

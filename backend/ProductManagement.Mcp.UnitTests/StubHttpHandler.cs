using System.Net;
using System.Text;

namespace ProductManagement.Mcp.UnitTests;

/// <summary>Records requests and answers them from a queue of canned responses.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public StubHttpHandler Respond(HttpStatusCode status, string json)
    {
        _responses.Enqueue(() => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_responses.Count > 0
            ? _responses.Dequeue()()
            : throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}"));
    }
}

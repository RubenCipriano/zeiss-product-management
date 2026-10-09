using System.Text;
using Microsoft.AspNetCore.Http;
using ProductManagement.Api.Idempotency;

namespace ProductManagement.Api.UnitTests.Idempotency;

public sealed class RequestFingerprintTests
{
    [Fact]
    public async Task TheSameRequest_HasTheSameFingerprint()
    {
        var first = await RequestFingerprint.ComputeAsync(Request("POST", "/api/products", "{\"name\":\"Lens\"}"));
        var second = await RequestFingerprint.ComputeAsync(Request("POST", "/api/products", "{\"name\":\"Lens\"}"));

        first.ShouldBe(second);
        first.Length.ShouldBe(64); // SHA-256, hex
    }

    [Theory]
    [InlineData("PUT", "/api/products", "", "{\"name\":\"Lens\"}")]
    [InlineData("POST", "/api/products/1", "", "{\"name\":\"Lens\"}")]
    [InlineData("POST", "/api/products", "?page=2", "{\"name\":\"Lens\"}")]
    [InlineData("POST", "/api/products", "", "{\"name\":\"Frame\"}")]
    public async Task AnyDifferenceInMethodPathQueryOrBody_ChangesTheFingerprint(
        string method,
        string path,
        string query,
        string body)
    {
        var original = await RequestFingerprint.ComputeAsync(Request("POST", "/api/products", "{\"name\":\"Lens\"}"));

        var other = await RequestFingerprint.ComputeAsync(Request(method, path, body, query));

        other.ShouldNotBe(original);
    }

    [Fact]
    public async Task TheBodyCanStillBeReadAfterwards()
    {
        var request = Request("POST", "/api/products", "{\"name\":\"Lens\"}");

        await RequestFingerprint.ComputeAsync(request);

        using var reader = new StreamReader(request.Body);
        (await reader.ReadToEndAsync(TestContext.Current.CancellationToken)).ShouldBe("{\"name\":\"Lens\"}");
    }

    private static HttpRequest Request(string method, string path, string body, string query = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context.Request;
    }
}

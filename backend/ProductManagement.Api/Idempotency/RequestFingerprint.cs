using System.Security.Cryptography;
using System.Text;

namespace ProductManagement.Api.Idempotency;

/// <summary>
/// What makes two requests "the same" for an idempotency key: method, path, query and body. The same key with a
/// different request is detected and refused.
/// </summary>
internal static class RequestFingerprint
{
    /// <summary>Hashes the request; the body stays readable for the endpoint (buffered and rewound).</summary>
    public static async Task<string> ComputeAsync(HttpRequest request)
    {
        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
        request.Body.Position = 0;

        var material = $"{request.Method}\n{request.Path}{request.QueryString}\n{body}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}

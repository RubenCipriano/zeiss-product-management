using ModelContextProtocol;
using ProductManagement.Mcp.Api;

namespace ProductManagement.Mcp.Tools;

internal static class ToolErrors
{
    /// <summary>
    /// Runs an API call and turns an API rejection into an MCP tool error the assistant can read and act on
    /// (e.g. "only 3 available"), instead of a generic failure.
    /// </summary>
    public static async Task<T> CallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (ProductApiException exception)
        {
            var code = exception.ErrorCode is null ? string.Empty : $" [{exception.ErrorCode}]";
            throw new McpException($"{exception.Message}{code}", exception);
        }
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProductManagement.Mcp.Api;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol messages, so every log line must go to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddOptions<ProductApiOptions>()
    .Bind(builder.Configuration.GetSection(ProductApiOptions.SectionName))
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.ClientSecret),
        "ProductApi__ClientSecret is not set: the MCP server needs its client secret to sign in to the identity provider.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AccessTokenProvider>();
builder.Services.AddTransient<BearerTokenHandler>();
builder.Services.AddHttpClient(AccessTokenProvider.HttpClientName);
builder.Services
    .AddHttpClient<ProductApiClient>((services, http) =>
        http.BaseAddress = services.GetRequiredService<IOptions<ProductApiOptions>>().Value.BaseUrl)
    .AddHttpMessageHandler<BearerTokenHandler>();

// Tools are discovered from [McpServerToolType] classes in this assembly (see Tools/).
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

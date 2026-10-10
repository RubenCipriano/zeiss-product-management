namespace ProductManagement.Api.Observability;

/// <summary>Logging setup for the API (<c>AddApiLogging</c> in <c>Program.cs</c>).</summary>
internal static class LoggingExtensions
{
    /// <summary>
    /// Logs are the system's observability signal: one JSON object per line on stdout (appsettings.json), collected
    /// into Loki and read in Grafana. Every line carries the trace id of its request. The gateway sends a W3C
    /// <c>traceparent</c> header with each request and logs the same id, so one search by trace id shows the gateway's
    /// line and every line the API wrote for that request.
    /// </summary>
    public static WebApplicationBuilder AddApiLogging(this WebApplicationBuilder builder)
    {
        builder.Logging.Configure(options =>
            options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        return builder;
    }
}

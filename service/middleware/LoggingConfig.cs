using Microsoft.Extensions.Logging.Console;

namespace LlmOps.Middleware;

public sealed class InfrastructureLoggingOptions
{
    public string ServiceName { get; set; } = "llmops-service";
    public string TimestampFormat { get; set; } = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    public bool IncludeCategory { get; set; } = true;
    public bool IncludeExceptionStackTrace { get; set; }
    public int DatabaseHealthIntervalSeconds { get; set; } = 5;
    public int DatabaseHealthTimeoutSeconds { get; set; } = 3;
    public string[] ExcludedPaths { get; set; } = [];
    public string[] StateFields { get; set; } = ["dependency", "operation", "duration_ms", "address", "connection_id", "request_id"];
    public Dictionary<string, string> FrameworkMessages { get; set; } = new()
    {
        ["Microsoft.AspNetCore.Server.Kestrel:13"] = "Unhandled application exception"
    };
}

public static class LoggingConfig
{
    public static void AddInfrastructureLogging(this WebApplicationBuilder builder, string[] args)
    {
        builder.Configuration.AddJsonFile("middleware/logging_config.json",
            optional: false, reloadOnChange: true);
        // Environment variables and command-line arguments retain their precedence.
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(args);
        builder.Services.Configure<InfrastructureLoggingOptions>(
            builder.Configuration.GetSection("InfrastructureLogging"));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<PostgresAvailability>();
        builder.Services.AddSingleton<PostgresOperationLogger>();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.FormatterName = FlatJsonConsoleFormatter.FormatterName);
        builder.Logging.AddConsoleFormatter<FlatJsonConsoleFormatter, ConsoleFormatterOptions>();
    }
}

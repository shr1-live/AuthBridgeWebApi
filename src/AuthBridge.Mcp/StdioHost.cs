using AuthBridge.Application;
using AuthBridge.Application.Common;
using AuthBridge.Application.Services;
using AuthBridge.Infrastructure;
using AuthBridge.Mcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace AuthBridge.Mcp;

/// <summary>
/// Local stdio MCP host. Trusted local-process mode: it acts as one configured seeded
/// subject, so it refuses to start outside the Development environment. stdout carries
/// only protocol messages; every log line goes to stderr.
/// </summary>
public static class StdioHost
{
    public static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        if (!builder.Environment.IsDevelopment())
        {
            await Console.Error.WriteLineAsync("AuthBridge stdio MCP runs only with DOTNET_ENVIRONMENT=Development. Use the authenticated /mcp endpoint elsewhere.");
            return 1;
        }

        builder.Services.AddAuthBridgeApplication(builder.Configuration);
        builder.Services.AddAuthBridgePersistence(builder.Configuration);
        builder.Services.AddScoped<IMcpCallerAccessor, LocalDevelopmentCallerAccessor>();
        builder.Services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "authbridge-local", Version = "1.0.0" })
            .WithStdioServerTransport()
            .WithTools<AuthBridgeTools>();

        await builder.Build().RunAsync();
        return 0;
    }
}

/// <summary>Resolves Mcp:LocalUserSubject through UserAccess; missing or inactive subjects are refused.</summary>
public sealed class LocalDevelopmentCallerAccessor(
    IConfiguration configuration,
    IHostEnvironment environment,
    ICallerContextResolver resolver) : IMcpCallerAccessor
{
    public Task<Result<CallerContext>> GetCallerAsync(string correlationId, CancellationToken ct)
    {
        if (!environment.IsDevelopment())
            return Task.FromResult(Result<CallerContext>.Failure(Errors.Forbidden("Local MCP identity is disabled outside Development.")));
        var subject = configuration["Mcp:LocalUserSubject"];
        if (string.IsNullOrWhiteSpace(subject))
            return Task.FromResult(Result<CallerContext>.Failure(Errors.AccessNotProvisioned()));
        return resolver.ResolveAsync(subject, correlationId, ct);
    }
}

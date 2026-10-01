using AuthBridge.Api.Assistant;
using AuthBridge.Api.Auth;
using AuthBridge.Api.Infrastructure;
using AuthBridge.Api.Mcp;
using AuthBridge.Api.Worker;
using AuthBridge.Application;
using AuthBridge.Application.Common;
using AuthBridge.Application.Services;
using AuthBridge.Infrastructure;
using AuthBridge.Mcp.Tools;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol.Protocol;

var builder = WebApplication.CreateBuilder(args);

// Render supplies PORT; bind every interface so the container is reachable.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));

// Report every missing setting at once, before anything reads them.
if (string.IsNullOrWhiteSpace(builder.Configuration["Database:ConnectionString"]))
    builder.Configuration["Database:ConnectionString"] = builder.Configuration["DATABASE_URL"];
if (string.IsNullOrWhiteSpace(builder.Configuration["Database:Provider"])
    && AuthBridge.Infrastructure.DependencyInjection.IsPostgresUrl(builder.Configuration["Database:ConnectionString"] ?? ""))
    builder.Configuration["Database:Provider"] = "Postgres";
// Demo mode with nothing configured: random signing key and an ephemeral SQLite database.
DemoMode.ApplyDefaults(builder.Configuration);
StartupConfiguration.Validate(builder.Configuration);

builder.Services.AddAuthBridgeApplication(builder.Configuration);
builder.Services.AddAuthBridgePersistence(builder.Configuration);
var authMode = builder.AddAuthBridgeAuthentication();
builder.AddAuthBridgeCors();
builder.AddAuthBridgeAssistant();

builder.Services
    .AddControllers()
    .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = context =>
    {
        var detail = string.Join(" ", context.ModelState
            .Where(e => e.Value?.Errors.Count > 0)
            .Select(e => $"{(string.IsNullOrEmpty(e.Key) ? "body" : e.Key)} is invalid."));
        var problem = ProblemWriter.Create(context.HttpContext, StatusCodes.Status400BadRequest, ErrorCodes.InvalidInput,
            string.IsNullOrEmpty(detail) ? "The request is invalid." : detail);
        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    });
builder.Services.AddOpenApi();

builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

// Hosted MCP: stateless Streamable HTTP, so nothing lives in memory between requests and a
// Render restart loses no sessions. Identity is bound per request from the bearer token.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IMcpCallerAccessor, HttpMcpCallerAccessor>();
builder.Services
    .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "authbridge", Version = "1.0.0" })
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<AuthBridgeTools>();

builder.Services.AddSingleton<SimulationSignal>();
builder.Services.AddSingleton<ISimulationSignal>(sp => sp.GetRequiredService<SimulationSignal>());
if (builder.Configuration.GetValue("Simulation:Enabled", true))
    builder.Services.AddHostedService<SimulationWorker>();

var app = builder.Build();

await DemoMode.PrepareDatabaseAsync(app);

app.UseSafeExceptionHandler();
app.UseCorrelation();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.CacheControl = "no-store";
    await next();
});

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready"),
    ResponseWriter = (context, report) => context.Response.WriteAsJsonAsync(new { status = report.Status.ToString() }),
}).AllowAnonymous();

app.MapControllers().RequireCors(CorsSetup.PolicyName);
app.MapMcp("/mcp").RequireAuthorization();

if (authMode == AuthMode.LocalDev && app.Environment.IsDevelopment())
{
    app.MapLocalDevAuth();
    app.MapOpenApi();
}
else if (authMode == AuthMode.Demo)
{
    app.MapLocalDevAuth("/demo").RequireRateLimiting(AssistantSetup.DemoSignInPolicy);
}

app.Run();

public partial class Program;

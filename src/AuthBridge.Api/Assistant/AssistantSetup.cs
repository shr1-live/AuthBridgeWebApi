using System.Threading.RateLimiting;
using AuthBridge.Api.Auth;
using AuthBridge.Api.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthBridge.Api.Assistant;

public static class AssistantSetup
{
    public const string ChatPolicy = "assistant-chat";
    public const string DemoSignInPolicy = "demo-sign-in";
    public const string RateLimited = "RATE_LIMITED";

    public static void AddAuthBridgeAssistant(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(AssistantOptions.Section);
        builder.Services.Configure<AssistantOptions>(section);
        builder.Services.PostConfigure<AssistantOptions>(o =>
        {
            if (string.IsNullOrWhiteSpace(o.ApiKey))
                o.ApiKey = builder.Configuration["ANTHROPIC_API_KEY"] ?? "";
        });
        builder.Services.AddHttpClient<IAssistantModel, AnthropicAssistantModel>(c => c.Timeout = TimeSpan.FromSeconds(90));
        builder.Services.AddHttpClient(nameof(ServerAddressLoopback), c => c.Timeout = TimeSpan.FromSeconds(60));
        builder.Services.AddSingleton<IMcpLoopback, ServerAddressLoopback>();
        builder.Services.AddSingleton<AssistantUsage>();
        builder.Services.AddScoped<AssistantService>();

        var hourly = section.GetValue(nameof(AssistantOptions.PerUserHourlyLimit), new AssistantOptions().PerUserHourlyLimit);
        builder.Services.AddRateLimiter(o =>
        {
            // Per signed-in user: the subject comes from the validated token.
            o.AddPolicy(ChatPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.Subject() ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = hourly, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
            // Per client address for the public demo sign-in. Render puts the client first in X-Forwarded-For.
            o.AddPolicy(DemoSignInPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                ClientAddress(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.OnRejected = async (rejected, ct) =>
            {
                var http = rejected.HttpContext;
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await http.Response.WriteAsJsonAsync(ProblemWriter.Create(http, StatusCodes.Status429TooManyRequests, RateLimited,
                    "Too many requests. Wait a little and try again."), options: null, contentType: "application/problem+json", ct);
            };
        });
    }

    private static string ClientAddress(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        var first = forwarded.Split(',', 2)[0].Trim();
        return first.Length > 0 ? first : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}

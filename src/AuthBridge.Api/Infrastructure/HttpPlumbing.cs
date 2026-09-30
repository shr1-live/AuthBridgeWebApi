using System.Diagnostics;
using System.Text.RegularExpressions;
using AuthBridge.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace AuthBridge.Api.Infrastructure;

public static partial class Correlation
{
    public const string Header = "X-Correlation-Id";
    private const string ItemKey = "authbridge.correlation";

    [GeneratedRegex("^[A-Za-z0-9._-]{8,64}$")]
    private static partial Regex Valid();

    public static string Get(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string id ? id : context.TraceIdentifier;

    /// <summary>
    /// Accepts a well-formed inbound correlation ID or creates one, echoes it on the response
    /// and logs one line per request. Never logs headers, query strings or bodies, so tokens
    /// cannot reach the logs.
    /// </summary>
    public static IApplicationBuilder UseCorrelation(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var inbound = context.Request.Headers[Header].ToString();
            var id = Valid().IsMatch(inbound) ? inbound : Guid.NewGuid().ToString("N");
            context.Items[ItemKey] = id;
            context.Response.Headers[Header] = id;

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AuthBridge.Http");
            using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id });
            var started = Stopwatch.GetTimestamp();
            await next();
            logger.LogInformation("{Method} {Path} -> {Status} in {Elapsed:0}ms",
                context.Request.Method, context.Request.Path.Value, context.Response.StatusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        });
}

public static class ProblemWriter
{
    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthenticated => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.Unprocessable => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static ProblemDetails Create(HttpContext context, int status, string code, string detail)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = code,
            Detail = detail,
            Type = "https://httpstatuses.io/" + status,
            Instance = context.Request.Path,
        };
        problem.Extensions["code"] = code;
        problem.Extensions["correlationId"] = Correlation.Get(context);
        return problem;
    }

    public static Task WriteAsync(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(Create(context, status, code, detail), (System.Text.Json.JsonSerializerOptions?)null,
            "application/problem+json");
    }

    /// <summary>Unhandled exceptions become a generic 500: no exception text, stack or SQL leaves the server.</summary>
    public static IApplicationBuilder UseSafeExceptionHandler(this IApplicationBuilder app) =>
        app.UseExceptionHandler(errorApp => errorApp.Run(context =>
            WriteAsync(context, StatusCodes.Status500InternalServerError, "INTERNAL_ERROR",
                "An unexpected error occurred. Quote the correlation ID when reporting it.")));
}

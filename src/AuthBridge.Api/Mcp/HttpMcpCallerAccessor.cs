using AuthBridge.Api.Auth;
using AuthBridge.Application.Common;
using AuthBridge.Application.Services;
using AuthBridge.Mcp.Tools;

namespace AuthBridge.Api.Mcp;

/// <summary>
/// Hosted MCP identity: the validated bearer token of the current HTTP request, mapped
/// through UserAccess. There is no fixed or fallback identity on this path.
/// </summary>
public sealed class HttpMcpCallerAccessor(IHttpContextAccessor http, ICallerContextResolver resolver) : IMcpCallerAccessor
{
    public Task<Result<CallerContext>> GetCallerAsync(string correlationId, CancellationToken ct)
    {
        var user = http.HttpContext?.User;
        if (user?.Subject() is not { } subject)
            return Task.FromResult(Result<CallerContext>.Failure(
                new AppError(ErrorCodes.Unauthenticated, "A valid bearer access token is required.", ErrorKind.Unauthenticated)));
        return resolver.ResolveAsync(subject, correlationId, ct);
    }
}

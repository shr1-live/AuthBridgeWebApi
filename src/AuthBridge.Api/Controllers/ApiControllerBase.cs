using AuthBridge.Api.Auth;
using AuthBridge.Api.Infrastructure;
using AuthBridge.Application.Common;
using AuthBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthBridge.Api.Controllers;

/// <summary>
/// Thin adapter base: resolve the trusted caller from the validated token, call one service
/// method, translate the result. No business rules live in controllers.
/// </summary>
[ApiController]
[Authorize]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    protected async Task<IActionResult> Run<T>(Func<CallerContext, CancellationToken, Task<Result<T>>> operation,
        Func<T, IActionResult>? onSuccess = null)
    {
        var ct = HttpContext.RequestAborted;
        var resolver = HttpContext.RequestServices.GetRequiredService<ICallerContextResolver>();
        var caller = await resolver.ResolveAsync(User.Subject(), Correlation.Get(HttpContext), ct);
        if (!caller.IsSuccess)
            return Problem(caller.Error!);

        var result = await operation(caller.Value!, ct);
        if (!result.IsSuccess)
            return Problem(result.Error!);
        return onSuccess is null ? Ok(result.Value) : onSuccess(result.Value!);
    }

    private ObjectResult Problem(AppError error)
    {
        var status = ProblemWriter.StatusFor(error.Kind);
        return new ObjectResult(ProblemWriter.Create(HttpContext, status, error.Code, error.Message))
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }

    protected static bool TryGuid(string value, out Guid id) => Guid.TryParse(value, out id) && id != Guid.Empty;

    protected IActionResult InvalidId(string name)
    {
        var problem = ProblemWriter.Create(HttpContext, StatusCodes.Status400BadRequest, ErrorCodes.InvalidInput, $"{name} must be a GUID.");
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest, ContentTypes = { "application/problem+json" } };
    }
}

using AuthBridge.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthBridge.Api.Assistant;

/// <summary>
/// The in-app assistant. The caller must be a provisioned user (checked by Run); the bearer
/// token is then forwarded to /mcp, which checks it again for every tool call.
/// </summary>
[Route("api/v1/assistant")]
public sealed class AssistantController(AssistantService assistant) : ApiControllerBase
{
    /// <summary>Whether the assistant is enabled, and the tools it would get from /mcp.</summary>
    [HttpGet]
    public Task<IActionResult> Describe() =>
        Run((_, ct) => assistant.DescribeAsync(BearerToken(), ct));

    [HttpPost("messages")]
    [EnableRateLimiting(AssistantSetup.ChatPolicy)]
    public Task<IActionResult> Send([FromBody] AssistantChatRequest body) =>
        Run((_, ct) => assistant.ChatAsync(BearerToken(), body.Messages, ct));

    private string BearerToken()
    {
        var header = Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : "";
    }
}

using System.ComponentModel;
using AuthBridge.Application.Common;
using AuthBridge.Application.Services;
using ModelContextProtocol.Server;

namespace AuthBridge.Mcp.Tools;

/// <summary>
/// Supplies the trusted caller for one tool invocation. The stdio host resolves a
/// Development-only configured subject; the HTTP host resolves the bearer token of the
/// current request. Tool arguments never influence identity.
/// </summary>
public interface IMcpCallerAccessor
{
    Task<Result<CallerContext>> GetCallerAsync(string correlationId, CancellationToken ct);
}

public sealed record ToolError(string Code, string Message);

/// <summary>Every tool result is labelled as simulation output and names its source.</summary>
public sealed record ToolEnvelope<T>(bool Ok, bool IsSimulation, string Source, T? Data, ToolError? Error)
{
    public const string SourceName = "AuthBridge synthetic demo (simulated payer; no real payer or patient data)";

    public static ToolEnvelope<T> From(Result<T> result) => result.IsSuccess
        ? new(true, true, SourceName, result.Value, null)
        : new(false, true, SourceName, default, new ToolError(result.Error!.Code, result.Error.Message));
}

/// <summary>
/// The eight AuthBridge tools. Deliberately absent: approval, file paths, SQL, shell,
/// generic HTTP and reset. Approval happens only through a human click in the Angular UI.
/// Each invocation gets its own DI scope, so services and DbContext are per call.
/// </summary>
[McpServerToolType]
public sealed class AuthBridgeTools(
    IMcpCallerAccessor callers,
    IAuthorizationQueryService queries,
    IRequirementQueryService requirements,
    IAuthorizationWorkflowService workflow,
    ISubmissionProposalService proposals,
    ISubmissionService submissions)
{
    public static readonly IReadOnlyList<string> ToolNames =
    [
        "get_authorization_status", "get_missing_documents", "get_authorization_history", "get_required_documents",
        "validate_authorization_request", "prepare_authorization_submission", "submit_authorization_request", "get_submission_status",
    ];

    [McpServerTool(Name = "get_authorization_status", Title = "Get authorization status", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns status, version, documents and pinned rule reference of a synthetic prior-authorization request (e.g. AUTH-104) in the caller's tenant. Read-only.")]
    public Task<ToolEnvelope<Application.Dtos.AuthorizationStatusDto>> GetAuthorizationStatus(
        [Description("Public authorization ID, e.g. AUTH-104")] string authorizationId, CancellationToken ct) =>
        Run(ct, caller => queries.GetStatusAsync(caller, authorizationId, ct));

    [McpServerTool(Name = "get_missing_documents", Title = "Get missing documents", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Compares the request's attached documents with its pinned requirement set and lists required, present, missing and invalid document types. Read-only.")]
    public Task<ToolEnvelope<Application.Dtos.MissingDocumentsDto>> GetMissingDocuments(
        [Description("Public authorization ID, e.g. AUTH-104")] string authorizationId, CancellationToken ct) =>
        Run(ct, caller => queries.GetMissingDocumentsAsync(caller, authorizationId, ct));

    [McpServerTool(Name = "get_authorization_history", Title = "Get authorization history", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns the request's status history ordered by time. Read-only.")]
    public Task<ToolEnvelope<Application.Dtos.PagedResult<Application.Dtos.HistoryEntryDto>>> GetAuthorizationHistory(
        [Description("Public authorization ID, e.g. AUTH-104")] string authorizationId,
        [Description("Page number, starting at 1")] int page = 1,
        [Description("Page size, 1 to 100")] int pageSize = 20,
        CancellationToken ct = default) =>
        Run(ct, caller => queries.GetHistoryAsync(caller, authorizationId, page, pageSize, ct));

    [McpServerTool(Name = "get_required_documents", Title = "Get required documents", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns the document types an exact, active fictional requirement set demands. Unknown or inactive rules return CONFIGURATION_MISSING rather than a guess. Read-only.")]
    public Task<ToolEnvelope<Application.Dtos.RequiredDocumentsDto>> GetRequiredDocuments(
        [Description("Payer code, e.g. DEMO-PAYER-A")] string payerCode,
        [Description("Service code, e.g. DEMO-MRI")] string serviceCode,
        [Description("Rule version, e.g. 1")] string ruleVersion,
        CancellationToken ct) =>
        Run(ct, caller => requirements.GetRequiredDocumentsAsync(caller, payerCode, serviceCode, ruleVersion, ct));

    [McpServerTool(Name = "validate_authorization_request", Title = "Validate authorization request", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Checks a pre-submission request against its pinned rule and records ReadyToSubmit or AwaitingDocuments. Requires the version returned by get_authorization_status.")]
    public Task<ToolEnvelope<Application.Dtos.ValidationResultDto>> ValidateAuthorizationRequest(
        [Description("Public authorization ID")] string authorizationId,
        [Description("Current request version (GUID) from get_authorization_status")] string expectedVersion,
        CancellationToken ct) =>
        Run(ct, caller => Guid.TryParse(expectedVersion, out var v)
            ? workflow.ValidateAsync(caller, authorizationId, v, ct)
            : Task.FromResult<Result<Application.Dtos.ValidationResultDto>>(Errors.Invalid("expectedVersion must be a GUID.")));

    [McpServerTool(Name = "prepare_authorization_submission", Title = "Prepare authorization submission", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Creates a five-minute submission proposal for a ReadyToSubmit request and returns its reviewUrl. A human must open that URL and click Approve in AuthBridge before submit_authorization_request can succeed; this tool cannot approve.")]
    public Task<ToolEnvelope<Application.Dtos.ProposalDto>> PrepareAuthorizationSubmission(
        [Description("Public authorization ID")] string authorizationId,
        [Description("Current request version (GUID) from get_authorization_status")] string expectedVersion,
        CancellationToken ct) =>
        Run(ct, caller => Guid.TryParse(expectedVersion, out var v)
            ? proposals.PrepareAsync(caller, authorizationId, v, ct)
            : Task.FromResult<Result<Application.Dtos.ProposalDto>>(Errors.Invalid("expectedVersion must be a GUID.")));

    [McpServerTool(Name = "submit_authorization_request", Title = "Submit authorization request", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Submits a proposal that a human has already approved in the AuthBridge UI to the simulated payer. Reusing the same idempotencyKey returns the original attempt instead of submitting twice.")]
    public Task<ToolEnvelope<Application.Dtos.SubmissionDto>> SubmitAuthorizationRequest(
        [Description("Proposal ID (GUID) from prepare_authorization_submission")] string proposalId,
        [Description("Caller-chosen key, up to 128 characters; reuse it when retrying")] string idempotencyKey,
        CancellationToken ct) =>
        Run(ct, caller => Guid.TryParse(proposalId, out var id)
            ? submissions.SubmitAsync(caller, id, idempotencyKey, ct)
            : Task.FromResult<Result<Application.Dtos.SubmissionDto>>(Errors.Invalid("proposalId must be a GUID.")));

    [McpServerTool(Name = "get_submission_status", Title = "Get submission status", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns the persisted state of a simulated submission attempt. Decisions appear only once the simulator has recorded them. Read-only.")]
    public Task<ToolEnvelope<Application.Dtos.SubmissionDto>> GetSubmissionStatus(
        [Description("Submission attempt ID (GUID)")] string attemptId, CancellationToken ct) =>
        Run(ct, caller => Guid.TryParse(attemptId, out var id)
            ? submissions.GetStatusAsync(caller, id, ct)
            : Task.FromResult<Result<Application.Dtos.SubmissionDto>>(Errors.Invalid("attemptId must be a GUID.")));

    private async Task<ToolEnvelope<T>> Run<T>(CancellationToken ct, Func<CallerContext, Task<Result<T>>> operation)
    {
        var correlationId = "mcp-" + Guid.NewGuid().ToString("N")[..16];
        var caller = await callers.GetCallerAsync(correlationId, ct);
        if (!caller.IsSuccess)
            return ToolEnvelope<T>.From(Result<T>.Failure(caller.Error!));
        return ToolEnvelope<T>.From(await operation(caller.Value!));
    }
}

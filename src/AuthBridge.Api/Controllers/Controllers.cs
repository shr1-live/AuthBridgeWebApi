using AuthBridge.Application.Services;
using AuthBridge.Domain.Rules;
using Microsoft.AspNetCore.Mvc;

namespace AuthBridge.Api.Controllers;

public sealed record AttachDocumentRequest(string DocumentType, string FixtureKey, Guid ExpectedVersion);

public sealed record ExpectedVersionRequest(Guid ExpectedVersion);

public sealed record SubmitRequest(Guid ProposalId, string? IdempotencyKey);

[Route("api/v1/authorizations")]
public sealed class AuthorizationsController(
    IAuthorizationQueryService queries,
    IDocumentService documents,
    IAuthorizationWorkflowService workflow,
    ISubmissionProposalService proposals) : ApiControllerBase
{
    [HttpGet]
    public Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? payerCode, [FromQuery] string? serviceCode,
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        Run((caller, ct) => queries.ListAsync(caller, new AuthorizationListQuery(status, payerCode, serviceCode, search), page, pageSize, ct));

    [HttpGet("{id}")]
    public Task<IActionResult> GetStatus(string id) =>
        Run((caller, ct) => queries.GetStatusAsync(caller, id, ct));

    [HttpGet("{id}/missing-documents")]
    public Task<IActionResult> GetMissingDocuments(string id) =>
        Run((caller, ct) => queries.GetMissingDocumentsAsync(caller, id, ct));

    [HttpGet("{id}/history")]
    public Task<IActionResult> GetHistory(string id, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        Run((caller, ct) => queries.GetHistoryAsync(caller, id, page, pageSize, ct));

    [HttpPost("{id}/documents")]
    public Task<IActionResult> AttachFixture(string id, [FromBody] AttachDocumentRequest body) =>
        Run((caller, ct) => documents.AttachFixtureAsync(caller, id, body.DocumentType, body.FixtureKey, body.ExpectedVersion, ct));

    [HttpPost("{id}/validate")]
    public Task<IActionResult> Validate(string id, [FromBody] ExpectedVersionRequest body) =>
        Run((caller, ct) => workflow.ValidateAsync(caller, id, body.ExpectedVersion, ct));

    [HttpPost("{id}/submission-proposals")]
    public Task<IActionResult> Prepare(string id, [FromBody] ExpectedVersionRequest body) =>
        Run((caller, ct) => proposals.PrepareAsync(caller, id, body.ExpectedVersion, ct),
            dto => Created($"/api/v1/submission-proposals/{dto.ProposalId}", dto));
}

[Route("api/v1/submission-proposals")]
public sealed class SubmissionProposalsController(ISubmissionProposalService proposals) : ApiControllerBase
{
    [HttpGet("{id}")]
    public Task<IActionResult> Get(string id) =>
        TryGuid(id, out var proposalId) ? Run((caller, ct) => proposals.GetAsync(caller, proposalId, ct)) : Task.FromResult(InvalidId("id"));

    /// <summary>The human approval boundary: called by the Angular review page after an explicit click.</summary>
    [HttpPost("{id}/approve")]
    public Task<IActionResult> Approve(string id) =>
        TryGuid(id, out var proposalId) ? Run((caller, ct) => proposals.ApproveAsync(caller, proposalId, ct)) : Task.FromResult(InvalidId("id"));
}

[Route("api/v1/submissions")]
public sealed class SubmissionsController(ISubmissionService submissions) : ApiControllerBase
{
    /// <summary>202 for a newly queued attempt, 200 for an idempotent replay of the original.</summary>
    [HttpPost]
    public Task<IActionResult> Submit([FromBody] SubmitRequest body, [FromHeader(Name = "Idempotency-Key")] string? headerKey) =>
        Run((caller, ct) => submissions.SubmitAsync(caller, body.ProposalId, body.IdempotencyKey ?? headerKey ?? "", ct),
            dto => dto.IsReplay ? Ok(dto) : Accepted($"/api/v1/submissions/{dto.AttemptId}", dto));

    [HttpGet("{id}")]
    public Task<IActionResult> GetStatus(string id) =>
        TryGuid(id, out var attemptId) ? Run((caller, ct) => submissions.GetStatusAsync(caller, attemptId, ct)) : Task.FromResult(InvalidId("id"));
}

[Route("api/v1")]
public sealed class ReferenceController(ICallerContextResolver callers, IRequirementQueryService requirements) : ApiControllerBase
{
    /// <summary>Who the server thinks the caller is: tenant and role come from UserAccess, not the token.</summary>
    [HttpGet("me")]
    public Task<IActionResult> Me() => Run((caller, ct) => callers.DescribeAsync(caller, ct));

    [HttpGet("requirements")]
    public Task<IActionResult> Requirements([FromQuery] string payerCode, [FromQuery] string serviceCode, [FromQuery] string ruleVersion) =>
        Run((caller, ct) => requirements.GetRequiredDocumentsAsync(caller, payerCode, serviceCode, ruleVersion, ct));

    /// <summary>The allowlisted synthetic fixtures the UI may attach. Metadata only.</summary>
    [HttpGet("document-fixtures")]
    public Task<IActionResult> Fixtures() =>
        Run((_, _) => Task.FromResult(Application.Common.Result<IReadOnlyList<DocumentFixture>>.Success(DocumentFixtureCatalog.All)));
}

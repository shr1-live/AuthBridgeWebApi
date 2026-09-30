using AuthBridge.Application.Common;
using AuthBridge.Application.Dtos;

namespace AuthBridge.Application.Services;

/// <summary>Optional list filter as received from a caller; validated by the service.</summary>
public sealed record AuthorizationListQuery(string? Status = null, string? PayerCode = null, string? ServiceCode = null, string? Search = null);

public interface ICallerContextResolver
{
    /// <summary>Maps a validated token subject to a trusted caller via active UserAccess.</summary>
    Task<Result<CallerContext>> ResolveAsync(string? subjectId, string correlationId, CancellationToken ct);

    Task<Result<CallerDto>> DescribeAsync(CallerContext caller, CancellationToken ct);
}

public interface IAuthorizationQueryService
{
    Task<Result<PagedResult<AuthorizationSummaryDto>>> ListAsync(CallerContext caller, AuthorizationListQuery filter, int page, int pageSize, CancellationToken ct);
    Task<Result<AuthorizationStatusDto>> GetStatusAsync(CallerContext caller, string authorizationId, CancellationToken ct);
    Task<Result<MissingDocumentsDto>> GetMissingDocumentsAsync(CallerContext caller, string authorizationId, CancellationToken ct);
    Task<Result<PagedResult<HistoryEntryDto>>> GetHistoryAsync(CallerContext caller, string authorizationId, int page, int pageSize, CancellationToken ct);
}

public interface IRequirementQueryService
{
    Task<Result<RequiredDocumentsDto>> GetRequiredDocumentsAsync(CallerContext caller, string payerCode, string serviceCode, string ruleVersion, CancellationToken ct);
}

public interface IDocumentService
{
    Task<Result<DocumentAttachmentDto>> AttachFixtureAsync(CallerContext caller, string authorizationId, string documentType, string fixtureKey, Guid expectedVersion, CancellationToken ct);
}

public interface IAuthorizationWorkflowService
{
    Task<Result<ValidationResultDto>> ValidateAsync(CallerContext caller, string authorizationId, Guid expectedVersion, CancellationToken ct);
}

public interface ISubmissionProposalService
{
    Task<Result<ProposalDto>> PrepareAsync(CallerContext caller, string authorizationId, Guid expectedVersion, CancellationToken ct);
    Task<Result<ProposalDto>> GetAsync(CallerContext caller, Guid proposalId, CancellationToken ct);

    /// <summary>
    /// Records the human approval. Called only from the authenticated Angular review page;
    /// deliberately not exposed as an MCP tool.
    /// </summary>
    Task<Result<ProposalDto>> ApproveAsync(CallerContext caller, Guid proposalId, CancellationToken ct);
}

public interface ISubmissionService
{
    Task<Result<SubmissionDto>> SubmitAsync(CallerContext caller, Guid proposalId, string idempotencyKey, CancellationToken ct);
    Task<Result<SubmissionDto>> GetStatusAsync(CallerContext caller, Guid attemptId, CancellationToken ct);
}

public interface IPayerSimulationService
{
    /// <summary>Advances one due attempt by one persisted step. Returns false when nothing was due.</summary>
    Task<bool> ProcessNextAsync(SimulationContext context, CancellationToken ct);
}

using AuthBridge.Domain;

namespace AuthBridge.Application.Dtos;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record CallerDto(string ActorId, string TenantId, string Role, bool CanWrite, string DisplayLabel);

public sealed record AuthorizationSummaryDto(
    string AuthorizationId,
    string Status,
    string PayerCode,
    string ServiceCode,
    string MemberLabel,
    Guid Version,
    DateTimeOffset UpdatedAtUtc,
    int RequiredDocumentCount,
    int ValidDocumentCount);

public sealed record RuleReferenceDto(Guid RequirementSetId, string PayerCode, string ServiceCode, string RuleVersion, bool IsActive, bool IsDemo);

public sealed record DocumentDto(string DocumentType, string FixtureKey, bool IsValid, DateTimeOffset CreatedAtUtc);

public sealed record AuthorizationStatusDto(
    string AuthorizationId,
    string TenantId,
    string Status,
    Guid Version,
    string PayerCode,
    string ServiceCode,
    string MemberLabel,
    string DemoScenario,
    RuleReferenceDto Rule,
    IReadOnlyList<DocumentDto> Documents,
    Guid? SubmissionAttemptId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record MissingDocumentsDto(
    string AuthorizationId,
    string RuleVersion,
    IReadOnlyList<string> Required,
    IReadOnlyList<string> Present,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Invalid,
    bool IsComplete);

public sealed record HistoryEntryDto(
    Guid Id,
    string? PreviousStatus,
    string NewStatus,
    string ActorId,
    string Reason,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId);

public sealed record RequiredDocumentsDto(
    string PayerCode,
    string ServiceCode,
    string RuleVersion,
    bool IsDemo,
    IReadOnlyList<string> RequiredDocumentTypes);

public sealed record DocumentAttachmentDto(
    string AuthorizationId,
    string DocumentType,
    string FixtureKey,
    bool IsValid,
    bool Replaced,
    string Status,
    Guid Version);

public sealed record ValidationResultDto(
    string AuthorizationId,
    string PreviousStatus,
    string Status,
    bool StatusChanged,
    Guid Version,
    MissingDocumentsDto Completeness);

public static class ProposalStates
{
    public const string PendingApproval = "PendingApproval";
    public const string Approved = "Approved";
    public const string Expired = "Expired";
    public const string Consumed = "Consumed";
    public const string Stale = "Stale";
}

public sealed record ProposalDto(
    Guid ProposalId,
    string AuthorizationId,
    string RequestStatus,
    string PayerCode,
    string ServiceCode,
    string RuleVersion,
    string MemberLabel,
    string SimulatedAction,
    string Summary,
    Guid ExpectedRequestVersion,
    string State,
    string ActorId,
    bool IsOwnedByCaller,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    DateTimeOffset? ConsumedAtUtc,
    string ReviewUrl);

public sealed record SubmissionDto(
    Guid AttemptId,
    string AuthorizationId,
    Guid ProposalId,
    string State,
    string RequestStatus,
    string? PayerReference,
    int FailureCount,
    string? LastError,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    bool IsReplay);

public static class DtoText
{
    public static string Of(AuthorizationStatus status) => status.ToString();
    public static string Of(AttemptState state) => state.ToString();
}

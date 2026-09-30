namespace AuthBridge.Domain.Entities;

public sealed class SyntheticMember
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string DisplayLabel { get; set; } = "";
}

public sealed class RequirementSet
{
    public Guid Id { get; set; }
    public string PayerCode { get; set; } = "";
    public string ServiceCode { get; set; } = "";
    public string RuleVersion { get; set; } = "";
    public bool IsActive { get; set; }
    public bool IsDemo { get; set; }
    public List<RequiredDocument> RequiredDocuments { get; set; } = [];
}

/// <summary>One required document type of a requirement set.</summary>
public sealed class RequiredDocument
{
    public Guid Id { get; set; }
    public Guid RequirementSetId { get; set; }
    public string DocumentType { get; set; } = "";
}

public sealed class AuthorizationRequest
{
    public Guid Id { get; set; }
    public string PublicId { get; set; } = "";
    public string TenantId { get; set; } = "";
    public Guid MemberId { get; set; }
    public string PayerCode { get; set; } = "";
    public string ServiceCode { get; set; } = "";
    public Guid RequirementSetId { get; set; }
    public AuthorizationStatus Status { get; set; }
    public DemoScenario DemoScenario { get; set; }
    public Guid Version { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public SyntheticMember? Member { get; set; }
    public RequirementSet? RequirementSet { get; set; }
    public List<RequestDocument> Documents { get; set; } = [];

    /// <summary>Replaces the concurrency token; called on every relevant mutation.</summary>
    public void Touch(DateTimeOffset now)
    {
        Version = Guid.NewGuid();
        UpdatedAtUtc = now;
    }
}

public sealed class RequestDocument
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public string DocumentType { get; set; } = "";
    public string FixtureKey { get; set; } = "";
    public bool IsValid { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AuthorizationHistory
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public AuthorizationStatus? PreviousStatus { get; set; }
    public AuthorizationStatus NewStatus { get; set; }
    public string ActorId { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string CorrelationId { get; set; } = "";
}

public sealed class SubmissionProposal
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public string TenantId { get; set; } = "";
    public string ActorId { get; set; } = "";
    public Guid ExpectedRequestVersion { get; set; }
    public string PayloadHash { get; set; } = "";
    public string Summary { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ApprovedAtUtc { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ConsumedAtUtc { get; set; }
    public Guid Version { get; set; }

    public AuthorizationRequest? Request { get; set; }
}

public sealed class SubmissionAttempt
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public string TenantId { get; set; } = "";
    public string ActorId { get; set; } = "";
    public Guid ProposalId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public AttemptState State { get; set; }
    public string? PayerReference { get; set; }
    public int FailureCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? ProcessingStartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    /// <summary>Earliest time the simulator may take the next step; null means not scheduled.</summary>
    public DateTimeOffset? NextStepAtUtc { get; set; }
    public Guid Version { get; set; }

    public AuthorizationRequest? Request { get; set; }

    public void Touch(DateTimeOffset now)
    {
        Version = Guid.NewGuid();
        UpdatedAtUtc = now;
    }
}

/// <summary>
/// Server-managed mapping from a verified Supabase user subject to a tenant and role.
/// The subject is a logical reference; Supabase auth.users lives in another database.
/// </summary>
public sealed class UserAccess
{
    public string SubjectId { get; set; } = "";
    public string TenantId { get; set; } = "";
    public UserRole Role { get; set; }
    public bool IsActive { get; set; }
    public string DisplayLabel { get; set; } = "";
}

public sealed class AuditEvent
{
    public Guid Id { get; set; }
    public string ActorId { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Operation { get; set; } = "";
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string Outcome { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string CorrelationId { get; set; } = "";
}

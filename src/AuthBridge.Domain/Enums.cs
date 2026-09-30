namespace AuthBridge.Domain;

// Stored as plain strings in both providers; never as database enums.

public enum AuthorizationStatus
{
    Draft,
    AwaitingDocuments,
    ReadyToSubmit,
    Submitted,
    UnderReview,
    Approved,
    Denied,
}

public enum AttemptState
{
    Queued,
    Processing,
    Completed,
    Failed,
}

public enum UserRole
{
    Viewer,
    Coordinator,
}

/// <summary>
/// Explicit fixture scenario that decides the simulated payer outcome.
/// The outcome is never chosen by a model or by request text.
/// </summary>
public enum DemoScenario
{
    Approve,
    Deny,
    FailOnceThenApprove,
}

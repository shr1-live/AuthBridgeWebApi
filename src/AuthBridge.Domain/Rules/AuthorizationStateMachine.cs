namespace AuthBridge.Domain.Rules;

/// <summary>Pure definition of which status transitions the workflow allows.</summary>
public static class AuthorizationStateMachine
{
    private static readonly Dictionary<AuthorizationStatus, AuthorizationStatus[]> Allowed = new()
    {
        [AuthorizationStatus.Draft] = [AuthorizationStatus.AwaitingDocuments, AuthorizationStatus.ReadyToSubmit],
        [AuthorizationStatus.AwaitingDocuments] = [AuthorizationStatus.ReadyToSubmit],
        [AuthorizationStatus.ReadyToSubmit] = [AuthorizationStatus.AwaitingDocuments, AuthorizationStatus.Submitted],
        [AuthorizationStatus.Submitted] = [AuthorizationStatus.UnderReview],
        [AuthorizationStatus.UnderReview] = [AuthorizationStatus.Approved, AuthorizationStatus.Denied],
        [AuthorizationStatus.Approved] = [],
        [AuthorizationStatus.Denied] = [],
    };

    public static bool CanTransition(AuthorizationStatus from, AuthorizationStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>Statuses in which documents can change and validation can run.</summary>
    public static bool IsPreSubmission(AuthorizationStatus status) =>
        status is AuthorizationStatus.Draft or AuthorizationStatus.AwaitingDocuments or AuthorizationStatus.ReadyToSubmit;

    public static bool IsTerminal(AuthorizationStatus status) =>
        status is AuthorizationStatus.Approved or AuthorizationStatus.Denied;

    /// <summary>The status validation should produce; null when the request is past submission.</summary>
    public static AuthorizationStatus? ValidationTarget(AuthorizationStatus current, bool isComplete)
    {
        if (!IsPreSubmission(current))
            return null;
        return isComplete ? AuthorizationStatus.ReadyToSubmit : AuthorizationStatus.AwaitingDocuments;
    }
}

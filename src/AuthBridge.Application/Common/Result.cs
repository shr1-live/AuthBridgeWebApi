namespace AuthBridge.Application.Common;

/// <summary>Transport-neutral error category; the API maps it to an HTTP status, MCP reports the code.</summary>
public enum ErrorKind
{
    Validation,
    Unauthenticated,
    Forbidden,
    NotFound,
    Conflict,
    Unprocessable,
    /// <summary>A dependency (such as the assistant's model) is not configured or not reachable.</summary>
    Unavailable,
}

public sealed record AppError(string Code, string Message, ErrorKind Kind);

public static class ErrorCodes
{
    public const string InvalidInput = "INVALID_INPUT";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string Forbidden = "FORBIDDEN";
    public const string AccessNotProvisioned = "ACCESS_NOT_PROVISIONED";
    public const string NotFound = "NOT_FOUND";
    public const string VersionConflict = "VERSION_CONFLICT";
    public const string InvalidState = "INVALID_STATE";
    public const string AlreadySubmitted = "ALREADY_SUBMITTED";
    public const string ProposalNotApproved = "PROPOSAL_NOT_APPROVED";
    public const string ProposalExpired = "PROPOSAL_EXPIRED";
    public const string ProposalConsumed = "PROPOSAL_CONSUMED";
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";
    public const string MissingDocuments = "MISSING_DOCUMENTS";
    public const string ConfigurationMissing = "CONFIGURATION_MISSING";
}

public static class Errors
{
    public static AppError Invalid(string message) => new(ErrorCodes.InvalidInput, message, ErrorKind.Validation);
    public static AppError Forbidden(string message = "The caller's role does not permit this operation.") =>
        new(ErrorCodes.Forbidden, message, ErrorKind.Forbidden);
    public static AppError AccessNotProvisioned() =>
        new(ErrorCodes.AccessNotProvisioned, "No active access mapping exists for this user.", ErrorKind.Forbidden);
    // Deliberately identical for "missing" and "belongs to another tenant" so nothing leaks.
    public static AppError NotFound(string what) => new(ErrorCodes.NotFound, $"{what} was not found.", ErrorKind.NotFound);
    public static AppError VersionConflict(string message = "The record changed since it was read. Reload and retry.") =>
        new(ErrorCodes.VersionConflict, message, ErrorKind.Conflict);
    public static AppError InvalidState(string message) => new(ErrorCodes.InvalidState, message, ErrorKind.Conflict);
    public static AppError AlreadySubmitted() =>
        new(ErrorCodes.AlreadySubmitted, "This authorization already has a submission attempt.", ErrorKind.Conflict);
    public static AppError ProposalNotApproved() =>
        new(ErrorCodes.ProposalNotApproved, "The proposal has not been approved in the AuthBridge UI.", ErrorKind.Conflict);
    public static AppError ProposalExpired() =>
        new(ErrorCodes.ProposalExpired, "The proposal has expired. Prepare a new one.", ErrorKind.Conflict);
    public static AppError ProposalConsumed() =>
        new(ErrorCodes.ProposalConsumed, "The proposal has already been used for a submission.", ErrorKind.Conflict);
    public static AppError IdempotencyConflict() =>
        new(ErrorCodes.IdempotencyConflict, "This idempotency key was already used with a different payload.", ErrorKind.Conflict);
    public static AppError MissingDocuments(IEnumerable<string> missing) =>
        new(ErrorCodes.MissingDocuments, "Required documents are missing or invalid: " + string.Join(", ", missing) + ".", ErrorKind.Unprocessable);
    public static AppError ConfigurationMissing() =>
        new(ErrorCodes.ConfigurationMissing, "No active requirement set exists for this payer, service and rule version.", ErrorKind.Unprocessable);
}

public sealed class Result<T>
{
    private Result(T? value, AppError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }
    public AppError? Error { get; }
    public bool IsSuccess => Error is null;

    public static Result<T> Success(T value) => new(value, null);
    public static Result<T> Failure(AppError error) => new(default, error);

    public static implicit operator Result<T>(T value) => Success(value);
    public static implicit operator Result<T>(AppError error) => Failure(error);
}

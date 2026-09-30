using AuthBridge.Application.Common;
using AuthBridge.Application.Dtos;
using AuthBridge.Application.Persistence;
using AuthBridge.Domain;
using AuthBridge.Domain.Rules;

namespace AuthBridge.Application.Services;

/// <summary>Records the outcome of write operations, including refusals.</summary>
internal static class WriteAudit
{
    public static async Task<Result<T>> FailAsync<T>(IAuthBridgeStore store, TimeProvider time, CallerContext caller,
        string operation, string targetType, string? targetId, AppError error, CancellationToken ct)
    {
        store.DiscardChanges();
        store.AddAudit(Mapping.Audit(caller, operation, targetType, targetId, error.Code, time.GetUtcNow()));
        await store.SaveChangesAsync(ct);
        return error;
    }
}

public sealed class DocumentService(IAuthBridgeStore store, TimeProvider time) : IDocumentService
{
    private const string Operation = "AttachFixture";

    public async Task<Result<DocumentAttachmentDto>> AttachFixtureAsync(CallerContext caller, string authorizationId,
        string documentType, string fixtureKey, Guid expectedVersion, CancellationToken ct)
    {
        var result = await AttachCoreAsync(caller, authorizationId, documentType, fixtureKey, expectedVersion, ct);
        return result.IsSuccess || result.Error!.Kind == ErrorKind.Validation
            ? result
            : await WriteAudit.FailAsync<DocumentAttachmentDto>(store, time, caller, Operation, "Authorization", authorizationId, result.Error, ct);
    }

    private async Task<Result<DocumentAttachmentDto>> AttachCoreAsync(CallerContext caller, string authorizationId,
        string documentType, string fixtureKey, Guid expectedVersion, CancellationToken ct)
    {
        if (!caller.CanWrite)
            return Errors.Forbidden();
        if (InputRules.Code(authorizationId, "authorizationId", FieldLimits.PublicId) is { } e1)
            return e1;
        if (InputRules.Code(documentType, "documentType", FieldLimits.DocumentType) is { } e2)
            return e2;
        if (InputRules.Code(fixtureKey, "fixtureKey", FieldLimits.FixtureKey) is { } e3)
            return e3;
        if (InputRules.RequiredGuid(expectedVersion, "expectedVersion") is { } e4)
            return e4;
        if (!DocumentTypes.IsKnown(documentType))
            return Errors.Invalid("documentType is not a known document type.");
        var fixture = DocumentFixtureCatalog.Find(fixtureKey);
        if (fixture is null)
            return Errors.Invalid("fixtureKey is not an allowlisted fixture.");
        if (fixture.DocumentType != documentType)
            return Errors.Invalid($"Fixture {fixture.Key} is a {fixture.DocumentType}, not a {documentType}.");

        var request = await store.FindRequestAsync(caller.TenantId, authorizationId, ct);
        if (request is null)
            return Errors.NotFound("Authorization");
        if (request.Version != expectedVersion)
            return Errors.VersionConflict();
        if (!AuthorizationStateMachine.IsPreSubmission(request.Status))
            return Errors.InvalidState("Documents can only change before submission.");

        var now = time.GetUtcNow();
        var existing = request.Documents.FirstOrDefault(d => d.DocumentType == documentType);
        if (existing is null)
        {
            var doc = new Domain.Entities.RequestDocument
            {
                Id = Guid.NewGuid(),
                RequestId = request.Id,
                DocumentType = documentType,
                FixtureKey = fixture.Key,
                IsValid = fixture.IsValid,
                CreatedAtUtc = now,
            };
            store.AddDocument(doc);
            request.Documents.Add(doc);
        }
        else
        {
            existing.FixtureKey = fixture.Key;
            existing.IsValid = fixture.IsValid;
            existing.CreatedAtUtc = now;
        }

        // A new version invalidates every proposal prepared against the old one.
        request.Touch(now);

        if (request.Status == AuthorizationStatus.Draft)
        {
            Mapping.Transition(request, AuthorizationStatus.AwaitingDocuments, caller.ActorId,
                $"Document {documentType} attached", caller.CorrelationId, now, store.AddHistory);
        }
        else if (request.Status == AuthorizationStatus.ReadyToSubmit &&
                 (!request.RequirementSet!.IsActive || !Mapping.Evaluate(request).IsComplete))
        {
            Mapping.Transition(request, AuthorizationStatus.AwaitingDocuments, caller.ActorId,
                $"Document {documentType} replaced; requirements no longer satisfied", caller.CorrelationId, now, store.AddHistory);
        }

        store.AddAudit(Mapping.Audit(caller, Operation, "Authorization", request.PublicId, "Success", now));
        try
        {
            await store.SaveChangesAsync(ct);
        }
        catch (StoreConcurrencyException)
        {
            return Errors.VersionConflict();
        }
        catch (StoreUniqueConstraintException)
        {
            return Errors.VersionConflict("Another change attached this document type concurrently. Reload and retry.");
        }

        return new DocumentAttachmentDto(request.PublicId, documentType, fixture.Key, fixture.IsValid, existing is not null,
            DtoText.Of(request.Status), request.Version);
    }
}

public sealed class AuthorizationWorkflowService(IAuthBridgeStore store, TimeProvider time) : IAuthorizationWorkflowService
{
    private const string Operation = "Validate";

    public async Task<Result<ValidationResultDto>> ValidateAsync(CallerContext caller, string authorizationId, Guid expectedVersion, CancellationToken ct)
    {
        var result = await ValidateCoreAsync(caller, authorizationId, expectedVersion, ct);
        return result.IsSuccess || result.Error!.Kind == ErrorKind.Validation
            ? result
            : await WriteAudit.FailAsync<ValidationResultDto>(store, time, caller, Operation, "Authorization", authorizationId, result.Error, ct);
    }

    private async Task<Result<ValidationResultDto>> ValidateCoreAsync(CallerContext caller, string authorizationId, Guid expectedVersion, CancellationToken ct)
    {
        if (!caller.CanWrite)
            return Errors.Forbidden();
        if (InputRules.Code(authorizationId, "authorizationId", FieldLimits.PublicId) is { } e1)
            return e1;
        if (InputRules.RequiredGuid(expectedVersion, "expectedVersion") is { } e2)
            return e2;

        var request = await store.FindRequestAsync(caller.TenantId, authorizationId, ct);
        if (request is null)
            return Errors.NotFound("Authorization");
        if (request.Version != expectedVersion)
            return Errors.VersionConflict();
        if (!AuthorizationStateMachine.IsPreSubmission(request.Status))
            return Errors.InvalidState("Only requests that have not been submitted can be validated.");
        if (!request.RequirementSet!.IsActive)
            return Errors.ConfigurationMissing();

        var now = time.GetUtcNow();
        var completeness = Mapping.Evaluate(request);
        var previous = request.Status;
        var target = AuthorizationStateMachine.ValidationTarget(previous, completeness.IsComplete)!.Value;

        // Same-state validation is audited but does not fabricate a transition.
        if (target != previous)
        {
            var reason = completeness.IsComplete
                ? $"Validated against rule version {request.RequirementSet.RuleVersion}: complete"
                : $"Validated against rule version {request.RequirementSet.RuleVersion}: missing {string.Join(", ", completeness.Missing.Concat(completeness.Invalid))}";
            Mapping.Transition(request, target, caller.ActorId, reason, caller.CorrelationId, now, store.AddHistory);
        }

        store.AddAudit(Mapping.Audit(caller, Operation, "Authorization", request.PublicId,
            completeness.IsComplete ? "Complete" : "Incomplete", now));
        try
        {
            await store.SaveChangesAsync(ct);
        }
        catch (StoreConcurrencyException)
        {
            return Errors.VersionConflict();
        }

        return new ValidationResultDto(request.PublicId, DtoText.Of(previous), DtoText.Of(request.Status), target != previous,
            request.Version, Mapping.ToMissing(request, completeness));
    }
}

using AuthBridge.Application.Common;
using AuthBridge.Application.Dtos;
using AuthBridge.Application.Persistence;
using AuthBridge.Domain;

namespace AuthBridge.Application.Services;

public sealed class CallerContextResolver(IAuthBridgeStore store) : ICallerContextResolver
{
    public async Task<Result<CallerContext>> ResolveAsync(string? subjectId, string correlationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subjectId) || subjectId.Length > FieldLimits.ActorId)
            return new AppError(ErrorCodes.Unauthenticated, "The token has no usable subject.", ErrorKind.Unauthenticated);

        var access = await store.FindUserAccessAsync(subjectId, ct);
        if (access is null || !access.IsActive)
            return Errors.AccessNotProvisioned();

        return new CallerContext(access.SubjectId, access.TenantId, access.Role, correlationId);
    }

    public async Task<Result<CallerDto>> DescribeAsync(CallerContext caller, CancellationToken ct)
    {
        var access = await store.FindUserAccessAsync(caller.ActorId, ct);
        return new CallerDto(caller.ActorId, caller.TenantId, caller.Role.ToString(), caller.CanWrite, access?.DisplayLabel ?? "");
    }
}

/// <summary>Read-only queries. None of these methods add, modify or audit anything.</summary>
public sealed class AuthorizationQueryService(IAuthBridgeStore store) : IAuthorizationQueryService
{
    public async Task<Result<PagedResult<AuthorizationSummaryDto>>> ListAsync(
        CallerContext caller, AuthorizationListQuery filter, int page, int pageSize, CancellationToken ct)
    {
        if (InputRules.Paging(page, pageSize) is { } pagingError)
            return pagingError;

        AuthorizationStatus? status = null;
        if (!string.IsNullOrEmpty(filter.Status))
        {
            if (!Enum.GetNames<AuthorizationStatus>().Contains(filter.Status, StringComparer.Ordinal))
                return Errors.Invalid("status is not a known authorization status.");
            status = Enum.Parse<AuthorizationStatus>(filter.Status);
        }
        if (!string.IsNullOrEmpty(filter.PayerCode) && InputRules.Code(filter.PayerCode, "payerCode", FieldLimits.PayerCode) is { } e1)
            return e1;
        if (!string.IsNullOrEmpty(filter.ServiceCode) && InputRules.Code(filter.ServiceCode, "serviceCode", FieldLimits.ServiceCode) is { } e2)
            return e2;
        if (!string.IsNullOrEmpty(filter.Search) && InputRules.Code(filter.Search, "search", FieldLimits.PublicId) is { } e3)
            return e3;

        var (items, total) = await store.ListRequestsAsync(caller.TenantId,
            new AuthorizationListFilter(status, NullIfEmpty(filter.PayerCode), NullIfEmpty(filter.ServiceCode), NullIfEmpty(filter.Search)),
            page, pageSize, ct);
        return new PagedResult<AuthorizationSummaryDto>(items.Select(Mapping.ToSummary).ToList(), page, pageSize, total);
    }

    public async Task<Result<AuthorizationStatusDto>> GetStatusAsync(CallerContext caller, string authorizationId, CancellationToken ct)
    {
        if (InputRules.Code(authorizationId, "authorizationId", FieldLimits.PublicId) is { } error)
            return error;
        var request = await store.FindRequestAsync(caller.TenantId, authorizationId, ct);
        if (request is null)
            return Errors.NotFound("Authorization");
        var attempt = await store.FindAttemptForRequestAsync(request.Id, ct);
        return Mapping.ToStatus(request, attempt?.Id);
    }

    public async Task<Result<MissingDocumentsDto>> GetMissingDocumentsAsync(CallerContext caller, string authorizationId, CancellationToken ct)
    {
        if (InputRules.Code(authorizationId, "authorizationId", FieldLimits.PublicId) is { } error)
            return error;
        var request = await store.FindRequestAsync(caller.TenantId, authorizationId, ct);
        if (request is null)
            return Errors.NotFound("Authorization");
        // An inactive pinned rule is reported, never replaced with invented requirements.
        if (!request.RequirementSet!.IsActive)
            return Errors.ConfigurationMissing();
        return Mapping.ToMissing(request, Mapping.Evaluate(request));
    }

    public async Task<Result<PagedResult<HistoryEntryDto>>> GetHistoryAsync(
        CallerContext caller, string authorizationId, int page, int pageSize, CancellationToken ct)
    {
        if (InputRules.Code(authorizationId, "authorizationId", FieldLimits.PublicId) is { } error)
            return error;
        if (InputRules.Paging(page, pageSize) is { } pagingError)
            return pagingError;
        var request = await store.FindRequestAsync(caller.TenantId, authorizationId, ct);
        if (request is null)
            return Errors.NotFound("Authorization");
        var (items, total) = await store.ListHistoryAsync(request.Id, page, pageSize, ct);
        return new PagedResult<HistoryEntryDto>(items.Select(Mapping.ToHistory).ToList(), page, pageSize, total);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

public sealed class RequirementQueryService(IAuthBridgeStore store) : IRequirementQueryService
{
    public async Task<Result<RequiredDocumentsDto>> GetRequiredDocumentsAsync(
        CallerContext caller, string payerCode, string serviceCode, string ruleVersion, CancellationToken ct)
    {
        if (InputRules.Code(payerCode, "payerCode", FieldLimits.PayerCode) is { } e1)
            return e1;
        if (InputRules.Code(serviceCode, "serviceCode", FieldLimits.ServiceCode) is { } e2)
            return e2;
        if (InputRules.Code(ruleVersion, "ruleVersion", FieldLimits.RuleVersion) is { } e3)
            return e3;

        var set = await store.FindRequirementSetAsync(payerCode, serviceCode, ruleVersion, ct);
        if (set is null || !set.IsActive)
            return Errors.ConfigurationMissing();

        return new RequiredDocumentsDto(set.PayerCode, set.ServiceCode, set.RuleVersion, set.IsDemo,
            set.RequiredDocuments.Select(d => d.DocumentType).Order(StringComparer.Ordinal).ToList());
    }
}

using AuthBridge.Application.Common;
using AuthBridge.Application.Dtos;
using AuthBridge.Application.Persistence;
using AuthBridge.Domain;
using AuthBridge.Domain.Entities;
using AuthBridge.Domain.Rules;
using Microsoft.Extensions.Options;

namespace AuthBridge.Application.Services;

public sealed class SubmissionProposalService(IAuthBridgeStore store, TimeProvider time, IOptions<WorkflowOptions> options)
    : ISubmissionProposalService
{
    public async Task<Result<ProposalDto>> PrepareAsync(CallerContext caller, string authorizationId, Guid expectedVersion, CancellationToken ct)
    {
        var result = await PrepareCoreAsync(caller, authorizationId, expectedVersion, ct);
        return result.IsSuccess || result.Error!.Kind == ErrorKind.Validation
            ? result
            : await WriteAudit.FailAsync<ProposalDto>(store, time, caller, "PrepareSubmission", "Authorization", authorizationId, result.Error, ct);
    }

    private async Task<Result<ProposalDto>> PrepareCoreAsync(CallerContext caller, string authorizationId, Guid expectedVersion, CancellationToken ct)
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
            return Errors.AlreadySubmitted();
        if (!request.RequirementSet!.IsActive)
            return Errors.ConfigurationMissing();
        var completeness = Mapping.Evaluate(request);
        if (!completeness.IsComplete)
            return Errors.MissingDocuments(completeness.Missing.Concat(completeness.Invalid));
        if (request.Status != AuthorizationStatus.ReadyToSubmit)
            return Errors.InvalidState("Validate the request first; it must be ReadyToSubmit.");

        var now = time.GetUtcNow();
        var proposal = new SubmissionProposal
        {
            Id = Guid.NewGuid(),
            RequestId = request.Id,
            TenantId = request.TenantId,
            ActorId = caller.ActorId,
            ExpectedRequestVersion = request.Version,
            PayloadHash = PayloadHash(request),
            Summary = $"Submit {request.PublicId} ({request.ServiceCode}) to simulated payer {request.PayerCode} under rule version {request.RequirementSet.RuleVersion}.",
            CreatedAtUtc = now,
            ExpiresAtUtc = now + WorkflowOptions.ProposalLifetime,
            Version = Guid.NewGuid(),
            Request = request,
        };
        store.AddProposal(proposal);
        store.AddAudit(Mapping.Audit(caller, "PrepareSubmission", "Proposal", proposal.Id.ToString(), "Success", now));
        await store.SaveChangesAsync(ct);
        return ToDto(proposal, caller, now);
    }

    public async Task<Result<ProposalDto>> GetAsync(CallerContext caller, Guid proposalId, CancellationToken ct)
    {
        if (InputRules.RequiredGuid(proposalId, "proposalId") is { } e)
            return e;
        var proposal = await store.FindProposalAsync(proposalId, ct);
        if (proposal is null || proposal.TenantId != caller.TenantId)
            return Errors.NotFound("Proposal");
        return ToDto(proposal, caller, time.GetUtcNow());
    }

    public async Task<Result<ProposalDto>> ApproveAsync(CallerContext caller, Guid proposalId, CancellationToken ct)
    {
        var result = await ApproveCoreAsync(caller, proposalId, ct);
        return result.IsSuccess || result.Error!.Kind == ErrorKind.Validation
            ? result
            : await WriteAudit.FailAsync<ProposalDto>(store, time, caller, "ApproveProposal", "Proposal", proposalId.ToString(), result.Error, ct);
    }

    private async Task<Result<ProposalDto>> ApproveCoreAsync(CallerContext caller, Guid proposalId, CancellationToken ct)
    {
        if (!caller.CanWrite)
            return Errors.Forbidden();
        if (InputRules.RequiredGuid(proposalId, "proposalId") is { } e)
            return e;
        var proposal = await store.FindProposalAsync(proposalId, ct);
        if (proposal is null || proposal.TenantId != caller.TenantId)
            return Errors.NotFound("Proposal");
        if (proposal.ActorId != caller.ActorId)
            return Errors.Forbidden("Only the coordinator who prepared this proposal can approve it.");

        var now = time.GetUtcNow();
        if (proposal.ConsumedAtUtc is not null)
            return Errors.ProposalConsumed();
        if (now >= proposal.ExpiresAtUtc)
            return Errors.ProposalExpired();
        if (proposal.Request!.Version != proposal.ExpectedRequestVersion)
            return Errors.VersionConflict("The authorization changed after this proposal was prepared. Prepare a new one.");
        if (proposal.Request.Status != AuthorizationStatus.ReadyToSubmit)
            return Errors.InvalidState("The authorization is no longer ReadyToSubmit.");
        if (proposal.ApprovedAtUtc is not null)
            return ToDto(proposal, caller, now);

        proposal.ApprovedAtUtc = now;
        proposal.ApprovedBy = caller.ActorId;
        proposal.Version = Guid.NewGuid();
        store.AddAudit(Mapping.Audit(caller, "ApproveProposal", "Proposal", proposal.Id.ToString(), "Success", now));
        try
        {
            await store.SaveChangesAsync(ct);
        }
        catch (StoreConcurrencyException)
        {
            return Errors.VersionConflict();
        }
        return ToDto(proposal, caller, now);
    }

    internal static string PayloadHash(AuthorizationRequest r) =>
        InputRules.Sha256(string.Join('|',
            r.Id.ToString("N"), r.Version.ToString("N"), r.PayerCode, r.ServiceCode,
            r.RequirementSetId.ToString("N"), r.RequirementSet!.RuleVersion,
            string.Join(',', r.Documents.OrderBy(d => d.DocumentType, StringComparer.Ordinal)
                .Select(d => $"{d.DocumentType}:{d.FixtureKey}:{d.IsValid}"))));

    private ProposalDto ToDto(SubmissionProposal p, CallerContext caller, DateTimeOffset now)
    {
        var r = p.Request!;
        var state = p.ConsumedAtUtc is not null ? ProposalStates.Consumed
            : now >= p.ExpiresAtUtc ? ProposalStates.Expired
            : r.Version != p.ExpectedRequestVersion ? ProposalStates.Stale
            : p.ApprovedAtUtc is not null ? ProposalStates.Approved
            : ProposalStates.PendingApproval;
        return new ProposalDto(p.Id, r.PublicId, DtoText.Of(r.Status), r.PayerCode, r.ServiceCode,
            r.RequirementSet?.RuleVersion ?? "", r.Member?.DisplayLabel ?? "",
            $"Send a simulated prior-authorization submission to {r.PayerCode}. No real payer is contacted.",
            p.Summary, p.ExpectedRequestVersion, state, p.ActorId, p.ActorId == caller.ActorId,
            p.CreatedAtUtc, p.ExpiresAtUtc, p.ApprovedAtUtc, p.ConsumedAtUtc,
            $"{options.Value.ReviewUrlBase.TrimEnd('/')}/proposals/{p.Id}");
    }
}

public sealed class SubmissionService(IAuthBridgeStore store, TimeProvider time, IOptions<SimulationOptions> simulation, ISimulationSignal signal)
    : ISubmissionService
{
    private const string Operation = "Submit";

    public async Task<Result<SubmissionDto>> SubmitAsync(CallerContext caller, Guid proposalId, string idempotencyKey, CancellationToken ct)
    {
        var result = await SubmitCoreAsync(caller, proposalId, idempotencyKey, ct);
        return result.IsSuccess || result.Error!.Kind == ErrorKind.Validation
            ? result
            : await WriteAudit.FailAsync<SubmissionDto>(store, time, caller, Operation, "Proposal", proposalId.ToString(), result.Error, ct);
    }

    private async Task<Result<SubmissionDto>> SubmitCoreAsync(CallerContext caller, Guid proposalId, string idempotencyKey, CancellationToken ct)
    {
        if (!caller.CanWrite)
            return Errors.Forbidden();
        if (InputRules.RequiredGuid(proposalId, "proposalId") is { } e1)
            return e1;
        if (InputRules.IdempotencyKey(idempotencyKey) is { } e2)
            return e2;

        var payloadHash = SubmitPayloadHash(proposalId);

        // A matching idempotency record wins over every other check, so a retry returns the
        // original attempt even after the proposal has been consumed or has expired.
        if (await ReplayAsync(caller, idempotencyKey, payloadHash, ct) is { } replay)
            return replay;

        var proposal = await store.FindProposalAsync(proposalId, ct);
        if (proposal is null || proposal.TenantId != caller.TenantId)
            return Errors.NotFound("Proposal");
        if (proposal.ActorId != caller.ActorId)
            return Errors.Forbidden("Only the coordinator who prepared and approved this proposal can submit it.");

        var request = proposal.Request!;
        if (await store.FindAttemptForRequestAsync(request.Id, ct) is not null || !AuthorizationStateMachine.IsPreSubmission(request.Status))
            return Errors.AlreadySubmitted();

        var now = time.GetUtcNow();
        if (proposal.ApprovedAtUtc is null)
            return Errors.ProposalNotApproved();
        if (proposal.ConsumedAtUtc is not null)
            return Errors.ProposalConsumed();
        if (now >= proposal.ExpiresAtUtc)
            return Errors.ProposalExpired();
        if (request.Version != proposal.ExpectedRequestVersion)
            return Errors.VersionConflict("The authorization changed after this proposal was approved. Prepare a new one.");
        if (request.Status != AuthorizationStatus.ReadyToSubmit)
            return Errors.InvalidState("The authorization must be ReadyToSubmit.");
        if (!request.RequirementSet!.IsActive)
            return Errors.ConfigurationMissing();
        var completeness = Mapping.Evaluate(request);
        if (!completeness.IsComplete)
            return Errors.MissingDocuments(completeness.Missing.Concat(completeness.Invalid));

        var attempt = new SubmissionAttempt
        {
            Id = Guid.NewGuid(),
            RequestId = request.Id,
            TenantId = request.TenantId,
            ActorId = caller.ActorId,
            ProposalId = proposal.Id,
            IdempotencyKey = idempotencyKey,
            PayloadHash = payloadHash,
            State = AttemptState.Queued,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            NextStepAtUtc = now + simulation.Value.StepDelay,
            Version = Guid.NewGuid(),
        };

        // Attempt, Submitted state, proposal consumption, history and audit commit together.
        store.AddAttempt(attempt);
        Mapping.Transition(request, AuthorizationStatus.Submitted, caller.ActorId,
            $"Submitted to simulated payer {request.PayerCode}", caller.CorrelationId, now, store.AddHistory);
        proposal.ConsumedAtUtc = now;
        proposal.Version = Guid.NewGuid();
        store.AddAudit(Mapping.Audit(caller, Operation, "SubmissionAttempt", attempt.Id.ToString(), "Queued", now));

        try
        {
            await store.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is StoreUniqueConstraintException or StoreConcurrencyException)
        {
            // Lost a race. Never create a second attempt: resolve against what the winner stored.
            store.DiscardChanges();
            if (await ReplayAsync(caller, idempotencyKey, payloadHash, ct) is { } raced)
                return raced;
            return await store.FindAttemptForRequestAsync(request.Id, ct) is not null
                ? Errors.AlreadySubmitted()
                : Errors.VersionConflict();
        }

        signal.Notify();
        return Mapping.ToSubmission(attempt, request, isReplay: false);
    }

    private async Task<Result<SubmissionDto>?> ReplayAsync(CallerContext caller, string key, string payloadHash, CancellationToken ct)
    {
        var existing = await store.FindAttemptByIdempotencyKeyAsync(caller.TenantId, caller.ActorId, key, ct);
        if (existing is null)
            return null;
        if (existing.PayloadHash != payloadHash)
            return Errors.IdempotencyConflict();
        return Mapping.ToSubmission(existing, existing.Request!, isReplay: true);
    }

    public async Task<Result<SubmissionDto>> GetStatusAsync(CallerContext caller, Guid attemptId, CancellationToken ct)
    {
        if (InputRules.RequiredGuid(attemptId, "attemptId") is { } e)
            return e;
        var attempt = await store.FindAttemptAsync(attemptId, ct);
        if (attempt is null || attempt.TenantId != caller.TenantId)
            return Errors.NotFound("Submission");
        return Mapping.ToSubmission(attempt, attempt.Request!, isReplay: false);
    }

    public static string SubmitPayloadHash(Guid proposalId) => InputRules.Sha256("submit|" + proposalId.ToString("N"));
}

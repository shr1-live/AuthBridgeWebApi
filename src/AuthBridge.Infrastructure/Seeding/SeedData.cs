using System.Security.Cryptography;
using System.Text;
using AuthBridge.Domain;
using AuthBridge.Domain.Entities;
using AuthBridge.Domain.Rules;

namespace AuthBridge.Infrastructure.Seeding;

/// <summary>Development-only user subjects. Real Supabase users are mapped with the DbTool grant-access command.</summary>
public static class SeedUsers
{
    public const string TenantA = "TENANT-A";
    public const string TenantB = "TENANT-B";

    public const string CoordinatorA = "11111111-1111-4111-8111-111111111111";
    public const string ViewerA = "22222222-2222-4222-8222-222222222222";
    public const string CoordinatorB = "33333333-3333-4333-8333-333333333333";
    public const string InactiveA = "44444444-4444-4444-8444-444444444444";
    /// <summary>A second coordinator in tenant A, for wrong-actor proposal checks.</summary>
    public const string SecondCoordinatorA = "55555555-5555-4555-8555-555555555555";
}

public static class SeedIds
{
    public static Guid For(string name)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("authbridge-seed:" + name));
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    public static Guid Request(string publicId) => For("request:" + publicId);
    public static Guid Version(string publicId) => For("version:" + publicId);
    public static Guid Proposal(string publicId) => For("proposal:" + publicId);
    public static Guid Attempt(string publicId) => For("attempt:" + publicId);

    /// <summary>The approved proposal on AUTH-103 whose ExpectedRequestVersion is stale.</summary>
    public static readonly Guid StaleProposal = Proposal("AUTH-103");
}

public sealed class SeedSnapshot
{
    public List<SyntheticMember> Members { get; } = [];
    public List<RequirementSet> RequirementSets { get; } = [];
    public List<AuthorizationRequest> Requests { get; } = [];
    public List<AuthorizationHistory> History { get; } = [];
    public List<SubmissionProposal> Proposals { get; } = [];
    public List<SubmissionAttempt> Attempts { get; } = [];
    public List<UserAccess> Users { get; } = [];
}

/// <summary>
/// Deterministic synthetic fixture set: 34 requests across two tenants (20 in A, 14 in B). IDs are derived
/// from names so every reseed produces the same identifiers. Historic timestamps are fixed;
/// only the live stale-version proposal is anchored to the seeding clock.
/// </summary>
public static class SeedData
{
    public const string PayerA = "DEMO-PAYER-A";
    public const string PayerB = "DEMO-PAYER-B";
    public const string Mri = "DEMO-MRI";
    public const string Ct = "DEMO-CT";
    public const string Physio = "DEMO-PHYSIO";
    public const string Surgery = "DEMO-SURGERY";
    public const string Specialist = "DEMO-SPECIALIST";
    public const string Ultrasound = "DEMO-ULTRASOUND";
    public const string Rehab = "DEMO-REHAB";

    private static readonly DateTimeOffset Base = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private const string SeedActor = "system:seed";

    private static readonly Dictionary<string, string[]> Rules = new()
    {
        [Mri] = [DocumentTypes.ReferralLetter, DocumentTypes.ImagingReport],
        [Ct] = [DocumentTypes.ReferralLetter, DocumentTypes.ImagingReport],
        [Physio] = [DocumentTypes.ReferralLetter, DocumentTypes.TreatmentSummary],
        [Surgery] = [DocumentTypes.ReferralLetter, DocumentTypes.TreatmentSummary],
        [Specialist] = [DocumentTypes.ReferralLetter],
        [Ultrasound] = [DocumentTypes.ReferralLetter, DocumentTypes.ImagingReport],
        [Rehab] = [DocumentTypes.ReferralLetter, DocumentTypes.TreatmentSummary],
    };

    private sealed record Fixture(
        string PublicId, string Tenant, int Member, string Payer, string Service,
        AuthorizationStatus Status, DemoScenario Scenario, string[] Fixtures, bool InactiveRule = false);

    private static readonly Fixture[] Requests =
    [
        new("AUTH-101", SeedUsers.TenantA, 1, PayerA, Mri, AuthorizationStatus.Draft, DemoScenario.Approve, []),
        new("AUTH-102", SeedUsers.TenantA, 2, PayerB, Physio, AuthorizationStatus.AwaitingDocuments, DemoScenario.Approve, ["FX-REFERRAL-SIGNED"]),
        new("AUTH-103", SeedUsers.TenantA, 3, PayerA, Specialist, AuthorizationStatus.ReadyToSubmit, DemoScenario.Approve, ["FX-REFERRAL-SIGNED"]),
        new("AUTH-104", SeedUsers.TenantA, 1, PayerA, Mri, AuthorizationStatus.AwaitingDocuments, DemoScenario.Approve, ["FX-IMAGING-CURRENT"]),
        new("AUTH-105", SeedUsers.TenantA, 2, PayerA, Ct, AuthorizationStatus.ReadyToSubmit, DemoScenario.Approve, ["FX-REFERRAL-SIGNED", "FX-IMAGING-CURRENT"]),
        new("AUTH-106", SeedUsers.TenantA, 3, PayerB, Physio, AuthorizationStatus.ReadyToSubmit, DemoScenario.Deny, ["FX-REFERRAL-SIGNED", "FX-TREATMENT-COMPLETE"]),
        new("AUTH-107", SeedUsers.TenantA, 1, PayerB, Mri, AuthorizationStatus.Submitted, DemoScenario.Approve, ["FX-REFERRAL-SIGNED", "FX-IMAGING-CURRENT"]),
        new("AUTH-108", SeedUsers.TenantA, 2, PayerA, Ct, AuthorizationStatus.AwaitingDocuments, DemoScenario.Approve, ["FX-REFERRAL-SIGNED"], InactiveRule: true),
        new("AUTH-109", SeedUsers.TenantA, 3, PayerA, Specialist, AuthorizationStatus.Approved, DemoScenario.Approve, ["FX-REFERRAL-SIGNED"]),
        new("AUTH-110", SeedUsers.TenantA, 1, PayerA, Surgery, AuthorizationStatus.ReadyToSubmit, DemoScenario.FailOnceThenApprove, ["FX-REFERRAL-SIGNED", "FX-TREATMENT-COMPLETE"]),
        new("AUTH-111", SeedUsers.TenantA, 2, PayerB, Surgery, AuthorizationStatus.AwaitingDocuments, DemoScenario.Approve, ["FX-REFERRAL-SIGNED", "FX-TREATMENT-INCOMPLETE"]),
        new("AUTH-112", SeedUsers.TenantA, 3, PayerB, Ct, AuthorizationStatus.Denied, DemoScenario.Deny, ["FX-REFERRAL-SIGNED", "FX-IMAGING-CURRENT"]),
        // More everyday cases: a fresh draft, an out-of-date scan, an unsigned referral, and each outcome.
        new("AUTH-113", SeedUsers.TenantA, 7, PayerA, Ultrasound, AuthorizationStatus.Draft, DemoScenario.Approve, []),
        new("AUTH-114", SeedUsers.TenantA, 1, PayerB, Ultrasound, AuthorizationStatus.AwaitingDocuments, DemoScenario.Approve, ["FX-REFERRAL-SIGNED", "FX-IMAGING-EXPIRED"]),
        new("AUTH-115", SeedUsers.TenantA, 7, PayerA, Rehab, AuthorizationStatus.ReadyToSubmit, DemoScenario.Approve, ["FX-REFERRAL-SIGNED", "FX-TREATMENT-COMPLETE"]),
        new("AUTH-116", SeedUsers.TenantA, 2, PayerB, Rehab, AuthorizationStatus.AwaitingDocuments, DemoScenario.Approve, ["FX-REFERRAL-UNSIGNED", "FX-TREATMENT-COMPLETE"]),
        new("AUTH-117", SeedUsers.TenantA, 3, PayerA, Mri, AuthorizationStatus.ReadyToSubmit, DemoScenario.Deny, ["FX-REFERRAL-SIGNED", "FX-IMAGING-CURRENT"]),
        new("AUTH-118", SeedUsers.TenantA, 7, PayerB, Specialist, AuthorizationStatus.Approved, DemoScenario.Approve, ["FX-REFERRAL-SIGNED"]),
        new("AUTH-119", SeedUsers.TenantA, 1, PayerA, Physio, AuthorizationStatus.Denied, DemoScenario.Deny, ["FX-REFERRAL-SIGNED", "FX-TREATMENT-COMPLETE"]),
        new("AUTH-120", SeedUsers.TenantA, 2, PayerA, Ultrasound, AuthorizationStatus.ReadyToSubmit, DemoScenario.FailOnceThenApprove, ["FX-REFERRAL-SIGNED", "FX-IMAGING-CURRENT"]),
        new("AUTH-201", SeedUsers.TenantB, 4, PayerA, Specialist, AuthorizationStatus.Draft, DemoScenario.Approve, []),
        new("AUTH-202", SeedUsers.TenantB, 5, PayerB, Mri, AuthorizationStatus.AwaitingDocuments, DemoScenario.Approve, ["FX-REFERRAL-SIGNED"]),
        new("AUTH-203", SeedUsers.TenantB, 6, PayerA, Ct, AuthorizationStatus.ReadyToSubmit, DemoScenario.Approve, ["FX-REFERRAL-SIGNED", "FX-IMAGING-CURRENT"]),
        new("AUTH-204", SeedUsers.TenantB, 4, PayerA, Mri, AuthorizationStatus.AwaitingDocuments, DemoScenario.Approve, ["FX-IMAGING-CURRENT"]),
        new("AUTH-205", SeedUsers.TenantB, 5, PayerB, Physio, AuthorizationStatus.ReadyToSubmit, DemoScenario.Deny, ["FX-REFERRAL-SIGNED", "FX-TREATMENT-COMPLETE"]),
        new("AUTH-206", SeedUsers.TenantB, 6, PayerA, Surgery, AuthorizationStatus.Draft, DemoScenario.Approve, []),
        new("AUTH-207", SeedUsers.TenantB, 4, PayerB, Specialist, AuthorizationStatus.AwaitingDocuments, DemoScenario.Approve, ["FX-REFERRAL-UNSIGNED"]),
        new("AUTH-208", SeedUsers.TenantB, 5, PayerA, Surgery, AuthorizationStatus.ReadyToSubmit, DemoScenario.FailOnceThenApprove, ["FX-REFERRAL-SIGNED", "FX-TREATMENT-COMPLETE"]),
        new("AUTH-209", SeedUsers.TenantB, 8, PayerB, Ultrasound, AuthorizationStatus.ReadyToSubmit, DemoScenario.Approve, ["FX-REFERRAL-SIGNED", "FX-IMAGING-CURRENT"]),
        new("AUTH-210", SeedUsers.TenantB, 9, PayerA, Rehab, AuthorizationStatus.Draft, DemoScenario.Approve, []),
        new("AUTH-211", SeedUsers.TenantB, 10, PayerA, Ct, AuthorizationStatus.AwaitingDocuments, DemoScenario.Approve, ["FX-REFERRAL-SIGNED", "FX-IMAGING-EXPIRED"]),
        new("AUTH-212", SeedUsers.TenantB, 8, PayerB, Rehab, AuthorizationStatus.Approved, DemoScenario.Approve, ["FX-REFERRAL-SIGNED", "FX-TREATMENT-COMPLETE"]),
        new("AUTH-213", SeedUsers.TenantB, 9, PayerB, Mri, AuthorizationStatus.Denied, DemoScenario.Deny, ["FX-REFERRAL-SIGNED", "FX-IMAGING-CURRENT"]),
        new("AUTH-214", SeedUsers.TenantB, 10, PayerA, Specialist, AuthorizationStatus.ReadyToSubmit, DemoScenario.Approve, ["FX-REFERRAL-SIGNED"]),
    ];

    public static SeedSnapshot Build(DateTimeOffset now)
    {
        var s = new SeedSnapshot();

        // Synthetic member codes, deliberately not name-like.
        // Members 1-3 and 7 are tenant A; 4-6 and 8-10 are tenant B.
        string[] memberCodes = ["SYN-2904", "SYN-6612", "SYN-3388", "SYN-4471", "SYN-5120", "SYN-7731", "SYN-8015", "SYN-1293", "SYN-9046", "SYN-2257"];
        for (var i = 1; i <= memberCodes.Length; i++)
        {
            var tenant = i is <= 3 or 7 ? SeedUsers.TenantA : SeedUsers.TenantB;
            s.Members.Add(new SyntheticMember
            {
                Id = SeedIds.For("member:" + i),
                TenantId = tenant,
                DisplayLabel = memberCodes[i - 1],
            });
        }

        foreach (var payer in new[] { PayerA, PayerB })
        foreach (var (service, required) in Rules)
            s.RequirementSets.Add(RuleSet(payer, service, "1", isActive: true, required));
        // Retired, separate rule that AUTH-108 is pinned to: must never be replaced by v1.
        s.RequirementSets.Add(RuleSet(PayerA, Ct, "2", isActive: false, [DocumentTypes.ReferralLetter, DocumentTypes.ImagingReport, DocumentTypes.TreatmentSummary]));

        for (var index = 0; index < Requests.Length; index++)
            AddRequest(s, Requests[index], Base.AddHours(index * 6), now);

        s.Users.AddRange(
        [
            new UserAccess { SubjectId = SeedUsers.CoordinatorA, TenantId = SeedUsers.TenantA, Role = UserRole.Coordinator, IsActive = true, DisplayLabel = "Demo Coordinator (Tenant A)" },
            new UserAccess { SubjectId = SeedUsers.ViewerA, TenantId = SeedUsers.TenantA, Role = UserRole.Viewer, IsActive = true, DisplayLabel = "Demo Viewer (Tenant A)" },
            new UserAccess { SubjectId = SeedUsers.CoordinatorB, TenantId = SeedUsers.TenantB, Role = UserRole.Coordinator, IsActive = true, DisplayLabel = "Demo Coordinator (Tenant B)" },
            new UserAccess { SubjectId = SeedUsers.InactiveA, TenantId = SeedUsers.TenantA, Role = UserRole.Coordinator, IsActive = false, DisplayLabel = "Deactivated Coordinator (Tenant A)" },
            new UserAccess { SubjectId = SeedUsers.SecondCoordinatorA, TenantId = SeedUsers.TenantA, Role = UserRole.Coordinator, IsActive = true, DisplayLabel = "Second Coordinator (Tenant A)" },
        ]);
        return s;
    }

    private static RequirementSet RuleSet(string payer, string service, string version, bool isActive, string[] required)
    {
        var id = SeedIds.For($"rule:{payer}:{service}:{version}");
        return new RequirementSet
        {
            Id = id,
            PayerCode = payer,
            ServiceCode = service,
            RuleVersion = version,
            IsActive = isActive,
            IsDemo = true,
            RequiredDocuments = required.Select(t => new RequiredDocument
            {
                Id = SeedIds.For($"rule:{payer}:{service}:{version}:{t}"),
                RequirementSetId = id,
                DocumentType = t,
            }).ToList(),
        };
    }

    private static void AddRequest(SeedSnapshot s, Fixture f, DateTimeOffset created, DateTimeOffset now)
    {
        var ruleVersion = f.InactiveRule ? "2" : "1";
        var requestId = SeedIds.Request(f.PublicId);
        var request = new AuthorizationRequest
        {
            Id = requestId,
            PublicId = f.PublicId,
            TenantId = f.Tenant,
            MemberId = SeedIds.For("member:" + f.Member),
            PayerCode = f.Payer,
            ServiceCode = f.Service,
            RequirementSetId = SeedIds.For($"rule:{f.Payer}:{f.Service}:{ruleVersion}"),
            Status = f.Status,
            DemoScenario = f.Scenario,
            Version = SeedIds.Version(f.PublicId),
            CreatedAtUtc = created,
            Documents = f.Fixtures.Select(key =>
            {
                var fixture = DocumentFixtureCatalog.Find(key) ?? throw new InvalidOperationException($"Unknown seed fixture {key}");
                return new RequestDocument
                {
                    Id = SeedIds.For($"doc:{f.PublicId}:{fixture.DocumentType}"),
                    RequestId = requestId,
                    DocumentType = fixture.DocumentType,
                    FixtureKey = fixture.Key,
                    IsValid = fixture.IsValid,
                    CreatedAtUtc = created.AddMinutes(10),
                };
            }).ToList(),
        };
        s.Requests.Add(request);

        // Replay a plausible path to the current status, one minute apart so ordering is unambiguous.
        var path = PathTo(f.Status);
        var at = created;
        AuthorizationStatus? previous = null;
        foreach (var status in path)
        {
            s.History.Add(new AuthorizationHistory
            {
                Id = SeedIds.For($"history:{f.PublicId}:{status}"),
                RequestId = requestId,
                PreviousStatus = previous,
                NewStatus = status,
                ActorId = status is AuthorizationStatus.UnderReview or AuthorizationStatus.Approved or AuthorizationStatus.Denied
                    ? "system:payer-simulator" : SeedActor,
                Reason = previous is null ? "Synthetic request created" : $"Seeded transition to {status}",
                OccurredAtUtc = at,
                CorrelationId = "seed",
            });
            previous = status;
            at = at.AddMinutes(30);
        }
        request.UpdatedAtUtc = at.AddMinutes(-30);

        var submitted = f.Status is AuthorizationStatus.Submitted or AuthorizationStatus.UnderReview
            or AuthorizationStatus.Approved or AuthorizationStatus.Denied;
        var owner = f.Tenant == SeedUsers.TenantA ? SeedUsers.CoordinatorA : SeedUsers.CoordinatorB;

        if (submitted)
        {
            var submittedAt = created.AddHours(2);
            var proposal = new SubmissionProposal
            {
                Id = SeedIds.Proposal(f.PublicId),
                RequestId = requestId,
                TenantId = f.Tenant,
                ActorId = owner,
                ExpectedRequestVersion = SeedIds.For("version-before-submit:" + f.PublicId),
                PayloadHash = new string('0', 64),
                Summary = $"Seeded submission of {f.PublicId}",
                CreatedAtUtc = submittedAt.AddMinutes(-3),
                ExpiresAtUtc = submittedAt.AddMinutes(2),
                ApprovedAtUtc = submittedAt.AddMinutes(-2),
                ApprovedBy = owner,
                ConsumedAtUtc = submittedAt,
                Version = SeedIds.For("proposal-version:" + f.PublicId),
            };
            s.Proposals.Add(proposal);

            var terminal = f.Status is AuthorizationStatus.Approved or AuthorizationStatus.Denied;
            s.Attempts.Add(new SubmissionAttempt
            {
                Id = SeedIds.Attempt(f.PublicId),
                RequestId = requestId,
                TenantId = f.Tenant,
                ActorId = owner,
                ProposalId = proposal.Id,
                IdempotencyKey = "seed-" + f.PublicId,
                PayloadHash = Application.Services.SubmissionService.SubmitPayloadHash(proposal.Id),
                State = terminal ? AttemptState.Completed : AttemptState.Queued,
                PayerReference = terminal ? "SIM-SEED-" + f.PublicId[5..] : null,
                CreatedAtUtc = submittedAt,
                UpdatedAtUtc = terminal ? submittedAt.AddMinutes(30) : submittedAt,
                ProcessingStartedAtUtc = terminal ? submittedAt.AddMinutes(1) : null,
                CompletedAtUtc = terminal ? submittedAt.AddMinutes(30) : null,
                // A seeded queued attempt is due immediately so the worker picks it up.
                NextStepAtUtc = terminal ? null : submittedAt,
                Version = SeedIds.For("attempt-version:" + f.PublicId),
            });
        }

        if (f.PublicId == "AUTH-103")
        {
            // Approved, unexpired, but prepared against an older request version.
            s.Proposals.Add(new SubmissionProposal
            {
                Id = SeedIds.StaleProposal,
                RequestId = requestId,
                TenantId = f.Tenant,
                ActorId = owner,
                ExpectedRequestVersion = SeedIds.For("stale-version:AUTH-103"),
                PayloadHash = new string('0', 64),
                Summary = "Stale proposal: the request changed after it was prepared",
                CreatedAtUtc = now,
                ExpiresAtUtc = now.AddMinutes(5),
                ApprovedAtUtc = now,
                ApprovedBy = owner,
                Version = SeedIds.For("proposal-version:AUTH-103"),
            });
        }
    }

    private static AuthorizationStatus[] PathTo(AuthorizationStatus status) => status switch
    {
        AuthorizationStatus.Draft => [AuthorizationStatus.Draft],
        AuthorizationStatus.AwaitingDocuments => [AuthorizationStatus.Draft, AuthorizationStatus.AwaitingDocuments],
        AuthorizationStatus.ReadyToSubmit => [AuthorizationStatus.Draft, AuthorizationStatus.AwaitingDocuments, AuthorizationStatus.ReadyToSubmit],
        AuthorizationStatus.Submitted => [AuthorizationStatus.Draft, AuthorizationStatus.AwaitingDocuments, AuthorizationStatus.ReadyToSubmit, AuthorizationStatus.Submitted],
        AuthorizationStatus.UnderReview => [AuthorizationStatus.Draft, AuthorizationStatus.AwaitingDocuments, AuthorizationStatus.ReadyToSubmit, AuthorizationStatus.Submitted, AuthorizationStatus.UnderReview],
        _ => [AuthorizationStatus.Draft, AuthorizationStatus.AwaitingDocuments, AuthorizationStatus.ReadyToSubmit, AuthorizationStatus.Submitted, AuthorizationStatus.UnderReview, status],
    };
}

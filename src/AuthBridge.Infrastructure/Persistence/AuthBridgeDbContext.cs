using AuthBridge.Domain;
using AuthBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthBridge.Infrastructure.Persistence;

/// <summary>
/// Provider-neutral model. Everything here must mean the same thing on SQL Server and
/// PostgreSQL: Guid keys, string enums, bounded text, DateTimeOffset in UTC and an
/// application-managed Guid Version token (no rowversion, no xmin).
/// </summary>
public sealed class AuthBridgeDbContext(DbContextOptions<AuthBridgeDbContext> options) : DbContext(options)
{
    public const string Schema = "authbridge";

    public DbSet<SyntheticMember> Members => Set<SyntheticMember>();
    public DbSet<RequirementSet> RequirementSets => Set<RequirementSet>();
    public DbSet<RequiredDocument> RequiredDocuments => Set<RequiredDocument>();
    public DbSet<AuthorizationRequest> Requests => Set<AuthorizationRequest>();
    public DbSet<RequestDocument> Documents => Set<RequestDocument>();
    public DbSet<AuthorizationHistory> History => Set<AuthorizationHistory>();
    public DbSet<SubmissionProposal> Proposals => Set<SubmissionProposal>();
    public DbSet<SubmissionAttempt> Attempts => Set<SubmissionAttempt>();
    public DbSet<UserAccess> UserAccess => Set<UserAccess>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<SyntheticMember>(e =>
        {
            e.ToTable("SyntheticMembers");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).HasMaxLength(FieldLimits.TenantId).IsRequired();
            e.Property(x => x.DisplayLabel).HasMaxLength(FieldLimits.DisplayLabel).IsRequired();
        });

        b.Entity<RequirementSet>(e =>
        {
            e.ToTable("RequirementSets");
            e.HasKey(x => x.Id);
            e.Property(x => x.PayerCode).HasMaxLength(FieldLimits.PayerCode).IsRequired();
            e.Property(x => x.ServiceCode).HasMaxLength(FieldLimits.ServiceCode).IsRequired();
            e.Property(x => x.RuleVersion).HasMaxLength(FieldLimits.RuleVersion).IsRequired();
            e.HasIndex(x => new { x.PayerCode, x.ServiceCode, x.RuleVersion }).IsUnique();
            e.HasMany(x => x.RequiredDocuments).WithOne().HasForeignKey(x => x.RequirementSetId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RequiredDocument>(e =>
        {
            e.ToTable("RequiredDocuments");
            e.HasKey(x => x.Id);
            e.Property(x => x.DocumentType).HasMaxLength(FieldLimits.DocumentType).IsRequired();
            e.HasIndex(x => new { x.RequirementSetId, x.DocumentType }).IsUnique();
        });

        b.Entity<AuthorizationRequest>(e =>
        {
            e.ToTable("AuthorizationRequests");
            e.HasKey(x => x.Id);
            e.Property(x => x.PublicId).HasMaxLength(FieldLimits.PublicId).IsRequired();
            e.HasIndex(x => x.PublicId).IsUnique();
            e.Property(x => x.TenantId).HasMaxLength(FieldLimits.TenantId).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.Property(x => x.PayerCode).HasMaxLength(FieldLimits.PayerCode).IsRequired();
            e.Property(x => x.ServiceCode).HasMaxLength(FieldLimits.ServiceCode).IsRequired();
            StringEnum(e.Property(x => x.Status));
            StringEnum(e.Property(x => x.DemoScenario));
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Member).WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.RequirementSet).WithMany().HasForeignKey(x => x.RequirementSetId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Documents).WithOne().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RequestDocument>(e =>
        {
            e.ToTable("RequestDocuments");
            e.HasKey(x => x.Id);
            e.Property(x => x.DocumentType).HasMaxLength(FieldLimits.DocumentType).IsRequired();
            e.Property(x => x.FixtureKey).HasMaxLength(FieldLimits.FixtureKey).IsRequired();
            e.HasIndex(x => new { x.RequestId, x.DocumentType }).IsUnique();
        });

        b.Entity<AuthorizationHistory>(e =>
        {
            e.ToTable("AuthorizationHistory");
            e.HasKey(x => x.Id);
            StringEnum(e.Property(x => x.PreviousStatus));
            StringEnum(e.Property(x => x.NewStatus));
            e.Property(x => x.ActorId).HasMaxLength(FieldLimits.ActorId).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(FieldLimits.Reason).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(FieldLimits.CorrelationId).IsRequired();
            e.HasOne<AuthorizationRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RequestId, x.OccurredAtUtc, x.Id });
        });

        b.Entity<SubmissionProposal>(e =>
        {
            e.ToTable("SubmissionProposals");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).HasMaxLength(FieldLimits.TenantId).IsRequired();
            e.Property(x => x.ActorId).HasMaxLength(FieldLimits.ActorId).IsRequired();
            e.Property(x => x.ApprovedBy).HasMaxLength(FieldLimits.ActorId);
            e.Property(x => x.PayloadHash).HasMaxLength(FieldLimits.PayloadHash).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(FieldLimits.Summary).IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Request).WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SubmissionAttempt>(e =>
        {
            e.ToTable("SubmissionAttempts");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).HasMaxLength(FieldLimits.TenantId).IsRequired();
            e.Property(x => x.ActorId).HasMaxLength(FieldLimits.ActorId).IsRequired();
            e.Property(x => x.IdempotencyKey).HasMaxLength(FieldLimits.IdempotencyKey).IsRequired();
            e.Property(x => x.PayloadHash).HasMaxLength(FieldLimits.PayloadHash).IsRequired();
            e.Property(x => x.PayerReference).HasMaxLength(FieldLimits.PayerReference);
            e.Property(x => x.LastError).HasMaxLength(FieldLimits.LastError);
            StringEnum(e.Property(x => x.State));
            e.Property(x => x.Version).IsConcurrencyToken();
            // One attempt per request, one per idempotency scope, one per consumed proposal.
            e.HasIndex(x => x.RequestId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.ActorId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => x.ProposalId).IsUnique();
            e.HasIndex(x => new { x.State, x.NextStepAtUtc });
            e.HasOne(x => x.Request).WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SubmissionProposal>().WithMany().HasForeignKey(x => x.ProposalId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<UserAccess>(e =>
        {
            e.ToTable("UserAccess");
            e.HasKey(x => x.SubjectId);
            e.Property(x => x.SubjectId).HasMaxLength(FieldLimits.ActorId);
            e.Property(x => x.TenantId).HasMaxLength(FieldLimits.TenantId).IsRequired();
            e.Property(x => x.DisplayLabel).HasMaxLength(FieldLimits.DisplayLabel).IsRequired();
            StringEnum(e.Property(x => x.Role));
        });

        b.Entity<AuditEvent>(e =>
        {
            e.ToTable("AuditEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.ActorId).HasMaxLength(FieldLimits.ActorId).IsRequired();
            e.Property(x => x.TenantId).HasMaxLength(FieldLimits.TenantId).IsRequired();
            e.Property(x => x.Operation).HasMaxLength(FieldLimits.Operation).IsRequired();
            e.Property(x => x.TargetType).HasMaxLength(FieldLimits.Operation);
            e.Property(x => x.TargetId).HasMaxLength(FieldLimits.Target);
            e.Property(x => x.Outcome).HasMaxLength(FieldLimits.Outcome).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(FieldLimits.CorrelationId).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.OccurredAtUtc });
        });
    }

    private static void StringEnum<T>(PropertyBuilder<T> property) =>
        property.HasConversion<string>().HasMaxLength(FieldLimits.EnumValue);
}

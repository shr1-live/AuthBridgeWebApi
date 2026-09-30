using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthBridge.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "authbridge");

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                schema: "authbridge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    TargetType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    TargetId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RequirementSets",
                schema: "authbridge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayerCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ServiceCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    RuleVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsDemo = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequirementSets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyntheticMembers",
                schema: "authbridge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DisplayLabel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyntheticMembers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserAccess",
                schema: "authbridge",
                columns: table => new
                {
                    SubjectId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DisplayLabel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAccess", x => x.SubjectId);
                });

            migrationBuilder.CreateTable(
                name: "RequiredDocuments",
                schema: "authbridge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequirementSetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequiredDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequiredDocuments_RequirementSets_RequirementSetId",
                        column: x => x.RequirementSetId,
                        principalSchema: "authbridge",
                        principalTable: "RequirementSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuthorizationRequests",
                schema: "authbridge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublicId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    MemberId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayerCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ServiceCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    RequirementSetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DemoScenario = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthorizationRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthorizationRequests_RequirementSets_RequirementSetId",
                        column: x => x.RequirementSetId,
                        principalSchema: "authbridge",
                        principalTable: "RequirementSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AuthorizationRequests_SyntheticMembers_MemberId",
                        column: x => x.MemberId,
                        principalSchema: "authbridge",
                        principalTable: "SyntheticMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AuthorizationHistory",
                schema: "authbridge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviousStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    NewStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthorizationHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthorizationHistory_AuthorizationRequests_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "authbridge",
                        principalTable: "AuthorizationRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RequestDocuments",
                schema: "authbridge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    FixtureKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    IsValid = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequestDocuments_AuthorizationRequests_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "authbridge",
                        principalTable: "AuthorizationRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubmissionProposals",
                schema: "authbridge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ExpectedRequestVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ApprovedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubmissionProposals_AuthorizationRequests_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "authbridge",
                        principalTable: "AuthorizationRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubmissionAttempts",
                schema: "authbridge",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PayloadHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    PayerReference = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    FailureCount = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProcessingStartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextStepAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubmissionAttempts_AuthorizationRequests_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "authbridge",
                        principalTable: "AuthorizationRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubmissionAttempts_SubmissionProposals_ProposalId",
                        column: x => x.ProposalId,
                        principalSchema: "authbridge",
                        principalTable: "SubmissionProposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TenantId_OccurredAtUtc",
                schema: "authbridge",
                table: "AuditEvents",
                columns: new[] { "TenantId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationHistory_RequestId_OccurredAtUtc_Id",
                schema: "authbridge",
                table: "AuthorizationHistory",
                columns: new[] { "RequestId", "OccurredAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationRequests_MemberId",
                schema: "authbridge",
                table: "AuthorizationRequests",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationRequests_PublicId",
                schema: "authbridge",
                table: "AuthorizationRequests",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationRequests_RequirementSetId",
                schema: "authbridge",
                table: "AuthorizationRequests",
                column: "RequirementSetId");

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationRequests_TenantId_Status",
                schema: "authbridge",
                table: "AuthorizationRequests",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RequestDocuments_RequestId_DocumentType",
                schema: "authbridge",
                table: "RequestDocuments",
                columns: new[] { "RequestId", "DocumentType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequiredDocuments_RequirementSetId_DocumentType",
                schema: "authbridge",
                table: "RequiredDocuments",
                columns: new[] { "RequirementSetId", "DocumentType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequirementSets_PayerCode_ServiceCode_RuleVersion",
                schema: "authbridge",
                table: "RequirementSets",
                columns: new[] { "PayerCode", "ServiceCode", "RuleVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionAttempts_ProposalId",
                schema: "authbridge",
                table: "SubmissionAttempts",
                column: "ProposalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionAttempts_RequestId",
                schema: "authbridge",
                table: "SubmissionAttempts",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionAttempts_State_NextStepAtUtc",
                schema: "authbridge",
                table: "SubmissionAttempts",
                columns: new[] { "State", "NextStepAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionAttempts_TenantId_ActorId_IdempotencyKey",
                schema: "authbridge",
                table: "SubmissionAttempts",
                columns: new[] { "TenantId", "ActorId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubmissionProposals_RequestId",
                schema: "authbridge",
                table: "SubmissionProposals",
                column: "RequestId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents",
                schema: "authbridge");

            migrationBuilder.DropTable(
                name: "AuthorizationHistory",
                schema: "authbridge");

            migrationBuilder.DropTable(
                name: "RequestDocuments",
                schema: "authbridge");

            migrationBuilder.DropTable(
                name: "RequiredDocuments",
                schema: "authbridge");

            migrationBuilder.DropTable(
                name: "SubmissionAttempts",
                schema: "authbridge");

            migrationBuilder.DropTable(
                name: "UserAccess",
                schema: "authbridge");

            migrationBuilder.DropTable(
                name: "SubmissionProposals",
                schema: "authbridge");

            migrationBuilder.DropTable(
                name: "AuthorizationRequests",
                schema: "authbridge");

            migrationBuilder.DropTable(
                name: "RequirementSets",
                schema: "authbridge");

            migrationBuilder.DropTable(
                name: "SyntheticMembers",
                schema: "authbridge");
        }
    }
}

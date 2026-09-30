using System.Net;
using AuthBridge.Infrastructure;
using AuthBridge.IntegrationTests.Harness;
using Microsoft.EntityFrameworkCore;

namespace AuthBridge.IntegrationTests;

[TestFixture(DatabaseProvider.SqlServer)]
[TestFixture(DatabaseProvider.Postgres)]
public class QueryAndDocumentTests(DatabaseProvider provider) : ProviderFixture(provider)
{
    [Test]
    public async Task Auth_104_status_missing_documents_and_history_are_correct()
    {
        var status = await StatusAsync("AUTH-104");
        Assert.Multiple(() =>
        {
            Assert.That(status.Str("status"), Is.EqualTo("AwaitingDocuments"));
            Assert.That(status.GetProperty("rule").Str("ruleVersion"), Is.EqualTo("1"));
            Assert.That(status.GetProperty("rule").GetProperty("isActive").GetBoolean(), Is.True);
        });

        var missing = await (await CoordinatorA.GetAsync("/api/v1/authorizations/AUTH-104/missing-documents")).JsonAsync();
        Assert.That(missing.GetProperty("missing").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "ReferralLetter" }));
        Assert.That(missing.GetProperty("present").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "ImagingReport" }));

        var history = await (await CoordinatorA.GetAsync("/api/v1/authorizations/AUTH-104/history")).JsonAsync();
        var items = history.GetProperty("items").EnumerateArray().ToList();
        Assert.That(items.Select(i => i.Str("newStatus")), Is.EqualTo(new[] { "Draft", "AwaitingDocuments" }));
        var times = items.Select(i => i.GetProperty("occurredAtUtc").GetDateTimeOffset()).ToList();
        Assert.That(times, Is.Ordered);
    }

    [Test]
    public async Task Reads_do_not_mutate_anything()
    {
        await using var before = App.Db();
        var version = (await before.Requests.SingleAsync(r => r.PublicId == "AUTH-104")).Version;
        var audits = await before.AuditEvents.CountAsync();
        var history = await before.History.CountAsync();

        foreach (var url in new[]
                 {
                     "/api/v1/authorizations", "/api/v1/authorizations/AUTH-104", "/api/v1/authorizations/AUTH-104/missing-documents",
                     "/api/v1/authorizations/AUTH-104/history", "/api/v1/requirements?payerCode=DEMO-PAYER-A&serviceCode=DEMO-MRI&ruleVersion=1",
                     "/api/v1/document-fixtures", "/api/v1/me",
                 })
            Assert.That((await CoordinatorA.GetAsync(url)).StatusCode, Is.EqualTo(HttpStatusCode.OK), url);

        await using var after = App.Db();
        Assert.Multiple(async () =>
        {
            Assert.That((await after.Requests.SingleAsync(r => r.PublicId == "AUTH-104")).Version, Is.EqualTo(version));
            Assert.That(await after.AuditEvents.CountAsync(), Is.EqualTo(audits));
            Assert.That(await after.History.CountAsync(), Is.EqualTo(history));
        });
    }

    [TestCase("page=0")]
    [TestCase("pageSize=0")]
    [TestCase("pageSize=101")]
    [TestCase("page=abc")]
    [TestCase("status=Pending")]
    [TestCase("status=1")]
    [TestCase("search=AUTH%27%3B--")]
    public async Task Invalid_list_parameters_are_400(string query)
    {
        var response = await CoordinatorA.GetAsync("/api/v1/authorizations?" + query);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.ProblemCodeAsync(), Is.EqualTo("INVALID_INPUT"));
    }

    [Test]
    public async Task List_paginates_and_filters_within_tenant()
    {
        var page1 = await (await CoordinatorA.GetAsync("/api/v1/authorizations?page=1&pageSize=5")).JsonAsync();
        var page3 = await (await CoordinatorA.GetAsync("/api/v1/authorizations?page=3&pageSize=5")).JsonAsync();
        Assert.Multiple(() =>
        {
            Assert.That(page1.GetProperty("total").GetInt32(), Is.EqualTo(12));
            Assert.That(page1.GetProperty("items").GetArrayLength(), Is.EqualTo(5));
            Assert.That(page3.GetProperty("items").GetArrayLength(), Is.EqualTo(2));
        });

        var ready = await (await CoordinatorA.GetAsync("/api/v1/authorizations?status=ReadyToSubmit&pageSize=100")).JsonAsync();
        Assert.That(ready.GetProperty("items").EnumerateArray().Select(i => i.Str("authorizationId")),
            Is.EquivalentTo(new[] { "AUTH-103", "AUTH-105", "AUTH-106", "AUTH-110" }));
    }

    [Test]
    public async Task List_rows_carry_document_progress_and_synthetic_member_codes()
    {
        var list = await (await CoordinatorA.GetAsync("/api/v1/authorizations?pageSize=100")).JsonAsync();
        var rows = list.GetProperty("items").EnumerateArray().ToDictionary(i => i.Str("authorizationId"));
        Assert.Multiple(() =>
        {
            Assert.That(rows["AUTH-104"].GetProperty("requiredDocumentCount").GetInt32(), Is.EqualTo(2));
            Assert.That(rows["AUTH-104"].GetProperty("validDocumentCount").GetInt32(), Is.EqualTo(1));
            Assert.That(rows["AUTH-105"].GetProperty("validDocumentCount").GetInt32(), Is.EqualTo(2));
            Assert.That(rows["AUTH-111"].GetProperty("validDocumentCount").GetInt32(), Is.EqualTo(1), "invalid fixture does not count");
            Assert.That(rows["AUTH-104"].Str("memberLabel"), Does.Match("^SYN-[0-9]{4}$"));
        });
    }

    [TestCase("AUTH-1044444444444444444444444444444444444")]
    [TestCase("AUTH 104")]
    [TestCase("-104")]
    public async Task Malformed_or_oversized_ids_are_400(string id)
    {
        var response = await CoordinatorA.GetAsync("/api/v1/authorizations/" + Uri.EscapeDataString(id));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Guid_routes_reject_non_guids_with_400()
    {
        Assert.That((await CoordinatorA.GetAsync("/api/v1/submission-proposals/not-a-guid")).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await CoordinatorA.GetAsync("/api/v1/submissions/123")).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Inactive_pinned_rule_reports_configuration_missing()
    {
        var response = await CoordinatorA.GetAsync("/api/v1/authorizations/AUTH-108/missing-documents");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
        Assert.That(await response.ProblemCodeAsync(), Is.EqualTo("CONFIGURATION_MISSING"));

        var validate = await ValidateAsync("AUTH-108", await VersionAsync("AUTH-108"));
        Assert.That(await validate.ProblemCodeAsync(), Is.EqualTo("CONFIGURATION_MISSING"));
        Assert.That((await StatusAsync("AUTH-108")).Str("status"), Is.EqualTo("AwaitingDocuments"), "no invented requirements, no transition");
    }

    [TestCase("DEMO-PAYER-A", "DEMO-CT", "2", Description = "inactive")]
    [TestCase("DEMO-PAYER-A", "DEMO-CT", "9", Description = "unknown version")]
    [TestCase("DEMO-PAYER-Z", "DEMO-MRI", "1", Description = "unknown payer")]
    public async Task Missing_or_inactive_requirement_sets_do_not_invent_rules(string payer, string service, string version)
    {
        var response = await CoordinatorA.GetAsync($"/api/v1/requirements?payerCode={payer}&serviceCode={service}&ruleVersion={version}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
        Assert.That(await response.ProblemCodeAsync(), Is.EqualTo("CONFIGURATION_MISSING"));
    }

    [TestCase("DEMO-MRI", new[] { "ImagingReport", "ReferralLetter" })]
    [TestCase("DEMO-CT", new[] { "ImagingReport", "ReferralLetter" })]
    [TestCase("DEMO-PHYSIO", new[] { "ReferralLetter", "TreatmentSummary" })]
    [TestCase("DEMO-SURGERY", new[] { "ReferralLetter", "TreatmentSummary" })]
    [TestCase("DEMO-SPECIALIST", new[] { "ReferralLetter" })]
    public async Task Version_1_requirements_match_the_specification(string service, string[] expected)
    {
        foreach (var payer in new[] { "DEMO-PAYER-A", "DEMO-PAYER-B" })
        {
            var body = await (await CoordinatorA.GetAsync($"/api/v1/requirements?payerCode={payer}&serviceCode={service}&ruleVersion=1")).JsonAsync();
            Assert.That(body.GetProperty("requiredDocumentTypes").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(expected), payer);
        }
    }

    [Test]
    public async Task Attaching_the_referral_touches_version_and_validation_makes_auth_104_ready()
    {
        var v1 = await VersionAsync("AUTH-104");
        var attach = await AttachAsync("AUTH-104", "ReferralLetter", "FX-REFERRAL-SIGNED", v1);
        Assert.That(attach.StatusCode, Is.EqualTo(HttpStatusCode.OK), await attach.Content.ReadAsStringAsync());
        var v2 = (await attach.JsonAsync()).Guid("version");
        Assert.That(v2, Is.Not.EqualTo(v1), "parent version replaced on document change");

        var validate = await ValidateAsync("AUTH-104", v2);
        var result = await validate.JsonAsync();
        Assert.Multiple(() =>
        {
            Assert.That(result.Str("status"), Is.EqualTo("ReadyToSubmit"));
            Assert.That(result.GetProperty("statusChanged").GetBoolean(), Is.True);
            Assert.That(result.GetProperty("completeness").GetProperty("isComplete").GetBoolean(), Is.True);
        });

        var history = await (await CoordinatorA.GetAsync("/api/v1/authorizations/AUTH-104/history")).JsonAsync();
        Assert.That(history.GetProperty("items").EnumerateArray().Last().Str("newStatus"), Is.EqualTo("ReadyToSubmit"));
    }

    [Test]
    public async Task Same_state_validation_audits_without_a_fake_transition()
    {
        var version = await VersionAsync("AUTH-104");
        await using var before = App.Db();
        var historyBefore = await before.History.CountAsync();

        var result = await (await ValidateAsync("AUTH-104", version)).JsonAsync();
        Assert.That(result.GetProperty("statusChanged").GetBoolean(), Is.False);
        Assert.That(result.Guid("version"), Is.EqualTo(version));

        await using var after = App.Db();
        Assert.That(await after.History.CountAsync(), Is.EqualTo(historyBefore));
        Assert.That(await after.AuditEvents.CountAsync(a => a.Operation == "Validate" && a.TargetId == "AUTH-104"), Is.EqualTo(1));
    }

    [Test]
    public async Task Stale_version_writes_are_409()
    {
        var stale = await VersionAsync("AUTH-104");
        await AttachAsync("AUTH-104", "ReferralLetter", "FX-REFERRAL-SIGNED", stale);
        var second = await AttachAsync("AUTH-104", "ReferralLetter", "FX-REFERRAL-UNSIGNED", stale);
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await second.ProblemCodeAsync(), Is.EqualTo("VERSION_CONFLICT"));
        Assert.That(await (await ValidateAsync("AUTH-104", stale)).ProblemCodeAsync(), Is.EqualTo("VERSION_CONFLICT"));
    }

    [TestCase("ReferralLetter", "FX-IMAGING-CURRENT", Description = "fixture of another type")]
    [TestCase("ReferralLetter", "FX-NOT-ALLOWLISTED")]
    [TestCase("ReferralLetter", "../../etc/passwd")]
    [TestCase("LabResult", "FX-REFERRAL-SIGNED", Description = "unknown document type")]
    public async Task Only_allowlisted_fixtures_of_the_right_type_attach(string type, string fixture)
    {
        var response = await AttachAsync("AUTH-104", type, fixture, await VersionAsync("AUTH-104"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.ProblemCodeAsync(), Is.EqualTo("INVALID_INPUT"));
    }

    [Test]
    public async Task Oversized_fields_are_rejected_before_the_database()
    {
        var response = await AttachAsync("AUTH-104", new string('D', 61), "FX-REFERRAL-SIGNED", await VersionAsync("AUTH-104"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        response = await AttachAsync("AUTH-104", "ReferralLetter", new string('F', 81), await VersionAsync("AUTH-104"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Malformed_json_body_is_400_problem()
    {
        var response = await CoordinatorA.PostAsync("/api/v1/authorizations/AUTH-104/validate",
            new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.ProblemCodeAsync(), Is.EqualTo("INVALID_INPUT"));
    }

    [Test]
    public async Task Replacing_a_document_with_an_invalid_one_moves_ready_request_back()
    {
        var response = await AttachAsync("AUTH-105", "ImagingReport", "FX-IMAGING-EXPIRED", await VersionAsync("AUTH-105"));
        var body = await response.JsonAsync();
        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("replaced").GetBoolean(), Is.True);
            Assert.That(body.GetProperty("isValid").GetBoolean(), Is.False);
            Assert.That(body.Str("status"), Is.EqualTo("AwaitingDocuments"));
        });
        var missing = await (await CoordinatorA.GetAsync("/api/v1/authorizations/AUTH-105/missing-documents")).JsonAsync();
        Assert.That(missing.GetProperty("invalid").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "ImagingReport" }));
        await using var db = App.Db();
        var request = await db.Requests.Include(r => r.Documents).SingleAsync(r => r.PublicId == "AUTH-105");
        Assert.That(request.Documents.Count(d => d.DocumentType == "ImagingReport"), Is.EqualTo(1), "replaced, not duplicated");
    }

    [Test]
    public async Task Documents_cannot_change_after_submission()
    {
        foreach (var id in new[] { "AUTH-107", "AUTH-109", "AUTH-112" })
        {
            var response = await AttachAsync(id, "ReferralLetter", "FX-REFERRAL-SIGNED", await VersionAsync(id));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), id);
            Assert.That(await response.ProblemCodeAsync(), Is.EqualTo("INVALID_STATE"));
        }
    }

    [Test]
    public async Task Terminal_requests_cannot_be_validated_or_resubmitted()
    {
        var validate = await ValidateAsync("AUTH-109", await VersionAsync("AUTH-109"));
        Assert.That(await validate.ProblemCodeAsync(), Is.EqualTo("INVALID_STATE"));
        var prepare = await PrepareAsync("AUTH-109", await VersionAsync("AUTH-109"));
        Assert.That(await prepare.ProblemCodeAsync(), Is.EqualTo("ALREADY_SUBMITTED"));
    }
}

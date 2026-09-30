using AuthBridge.Application.Common;
using AuthBridge.Domain;
using AuthBridge.Domain.Rules;

namespace AuthBridge.UnitTests;

public class CompletenessEvaluatorTests
{
    private static readonly string[] MriRule = [DocumentTypes.ReferralLetter, DocumentTypes.ImagingReport];

    [Test]
    public void Reports_missing_referral_when_only_imaging_is_present()
    {
        var result = CompletenessEvaluator.Evaluate(MriRule,
            [new PresentDocument(DocumentTypes.ImagingReport, "FX-IMAGING-CURRENT", true)]);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComplete, Is.False);
            Assert.That(result.Missing, Is.EqualTo(new[] { DocumentTypes.ReferralLetter }));
            Assert.That(result.Present, Is.EqualTo(new[] { DocumentTypes.ImagingReport }));
            Assert.That(result.Invalid, Is.Empty);
        });
    }

    [Test]
    public void Invalid_document_counts_as_invalid_not_present()
    {
        var result = CompletenessEvaluator.Evaluate(MriRule,
        [
            new PresentDocument(DocumentTypes.ReferralLetter, "FX-REFERRAL-UNSIGNED", false),
            new PresentDocument(DocumentTypes.ImagingReport, "FX-IMAGING-CURRENT", true),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsComplete, Is.False);
            Assert.That(result.Invalid, Is.EqualTo(new[] { DocumentTypes.ReferralLetter }));
            Assert.That(result.Missing, Is.Empty);
        });
    }

    [Test]
    public void Complete_when_every_required_type_is_valid_and_extras_are_ignored()
    {
        var result = CompletenessEvaluator.Evaluate([DocumentTypes.ReferralLetter],
        [
            new PresentDocument(DocumentTypes.ReferralLetter, "FX-REFERRAL-SIGNED", true),
            new PresentDocument(DocumentTypes.TreatmentSummary, "FX-TREATMENT-INCOMPLETE", false),
        ]);

        Assert.That(result.IsComplete, Is.True);
        Assert.That(result.Required, Is.EqualTo(new[] { DocumentTypes.ReferralLetter }));
    }

    [Test]
    public void Empty_rule_is_complete_and_does_not_invent_requirements()
    {
        var result = CompletenessEvaluator.Evaluate([], []);
        Assert.That(result.IsComplete, Is.True);
        Assert.That(result.Required, Is.Empty);
    }
}

public class AuthorizationStateMachineTests
{
    [TestCase(AuthorizationStatus.Draft, AuthorizationStatus.AwaitingDocuments, true)]
    [TestCase(AuthorizationStatus.Draft, AuthorizationStatus.ReadyToSubmit, true)]
    [TestCase(AuthorizationStatus.AwaitingDocuments, AuthorizationStatus.ReadyToSubmit, true)]
    [TestCase(AuthorizationStatus.ReadyToSubmit, AuthorizationStatus.AwaitingDocuments, true)]
    [TestCase(AuthorizationStatus.ReadyToSubmit, AuthorizationStatus.Submitted, true)]
    [TestCase(AuthorizationStatus.Submitted, AuthorizationStatus.UnderReview, true)]
    [TestCase(AuthorizationStatus.UnderReview, AuthorizationStatus.Approved, true)]
    [TestCase(AuthorizationStatus.UnderReview, AuthorizationStatus.Denied, true)]
    [TestCase(AuthorizationStatus.Draft, AuthorizationStatus.Submitted, false)]
    [TestCase(AuthorizationStatus.AwaitingDocuments, AuthorizationStatus.Submitted, false)]
    [TestCase(AuthorizationStatus.Submitted, AuthorizationStatus.Approved, false)]
    [TestCase(AuthorizationStatus.Approved, AuthorizationStatus.Submitted, false)]
    [TestCase(AuthorizationStatus.Denied, AuthorizationStatus.ReadyToSubmit, false)]
    [TestCase(AuthorizationStatus.Approved, AuthorizationStatus.Denied, false)]
    public void Allows_only_defined_transitions(AuthorizationStatus from, AuthorizationStatus to, bool expected) =>
        Assert.That(AuthorizationStateMachine.CanTransition(from, to), Is.EqualTo(expected));

    [Test]
    public void Terminal_states_have_no_outgoing_transitions()
    {
        foreach (var terminal in new[] { AuthorizationStatus.Approved, AuthorizationStatus.Denied })
        foreach (var target in Enum.GetValues<AuthorizationStatus>())
            Assert.That(AuthorizationStateMachine.CanTransition(terminal, target), Is.False, $"{terminal}->{target}");
    }

    [TestCase(AuthorizationStatus.Draft, true, AuthorizationStatus.ReadyToSubmit)]
    [TestCase(AuthorizationStatus.Draft, false, AuthorizationStatus.AwaitingDocuments)]
    [TestCase(AuthorizationStatus.ReadyToSubmit, false, AuthorizationStatus.AwaitingDocuments)]
    [TestCase(AuthorizationStatus.AwaitingDocuments, false, AuthorizationStatus.AwaitingDocuments)]
    public void Validation_target_depends_only_on_completeness(AuthorizationStatus current, bool complete, AuthorizationStatus expected) =>
        Assert.That(AuthorizationStateMachine.ValidationTarget(current, complete), Is.EqualTo(expected));

    [TestCase(AuthorizationStatus.Submitted)]
    [TestCase(AuthorizationStatus.UnderReview)]
    [TestCase(AuthorizationStatus.Approved)]
    [TestCase(AuthorizationStatus.Denied)]
    public void Validation_has_no_target_after_submission(AuthorizationStatus current) =>
        Assert.That(AuthorizationStateMachine.ValidationTarget(current, true), Is.Null);
}

public class DocumentFixtureCatalogTests
{
    [Test]
    public void Every_fixture_key_fits_the_column_and_uses_a_known_type()
    {
        foreach (var f in DocumentFixtureCatalog.All)
        {
            Assert.That(f.Key.Length, Is.LessThanOrEqualTo(FieldLimits.FixtureKey), f.Key);
            Assert.That(DocumentTypes.IsKnown(f.DocumentType), Is.True, f.Key);
        }
        Assert.That(DocumentFixtureCatalog.All.Select(f => f.Key), Is.Unique);
    }

    [Test]
    public void Unknown_or_path_like_keys_are_not_allowlisted()
    {
        Assert.That(DocumentFixtureCatalog.Find("../../etc/passwd"), Is.Null);
        Assert.That(DocumentFixtureCatalog.Find("fx-referral-signed"), Is.Null, "lookup is case-sensitive");
    }
}

public class InputRulesTests
{
    [TestCase(0, 10)]
    [TestCase(1, 0)]
    [TestCase(1, 101)]
    [TestCase(-5, 20)]
    public void Rejects_out_of_range_paging(int page, int pageSize) =>
        Assert.That(InputRules.Paging(page, pageSize)?.Code, Is.EqualTo(ErrorCodes.InvalidInput));

    [TestCase(1, 1)]
    [TestCase(3, 100)]
    public void Accepts_valid_paging(int page, int pageSize) =>
        Assert.That(InputRules.Paging(page, pageSize), Is.Null);

    [Test]
    public void Rejects_oversized_and_malformed_codes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(InputRules.Code(new string('A', 41), "id", FieldLimits.PublicId), Is.Not.Null);
            Assert.That(InputRules.Code("AUTH-104'; DROP TABLE x", "id", FieldLimits.PublicId), Is.Not.Null);
            Assert.That(InputRules.Code("", "id", FieldLimits.PublicId), Is.Not.Null);
            Assert.That(InputRules.Code("-AUTH", "id", FieldLimits.PublicId), Is.Not.Null);
            Assert.That(InputRules.Code("AUTH-104", "id", FieldLimits.PublicId), Is.Null);
            Assert.That(InputRules.Code(new string('A', 40), "id", FieldLimits.PublicId), Is.Null);
        });
    }

    [Test]
    public void Idempotency_key_bounds()
    {
        Assert.Multiple(() =>
        {
            Assert.That(InputRules.IdempotencyKey(new string('k', 128)), Is.Null);
            Assert.That(InputRules.IdempotencyKey(new string('k', 129)), Is.Not.Null);
            Assert.That(InputRules.IdempotencyKey("with space"), Is.Not.Null);
            Assert.That(InputRules.IdempotencyKey(null), Is.Not.Null);
            Assert.That(InputRules.IdempotencyKey("ui:2026-09-30T10:00:00.123_abc"), Is.Null);
        });
    }

    [Test]
    public void Sha256_is_stable_lowercase_hex()
    {
        Assert.That(InputRules.Sha256("abc"), Is.EqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
    }
}

public class CallerContextTests
{
    [Test]
    public void Only_coordinators_can_write()
    {
        Assert.That(new CallerContext("s", "TENANT-A", UserRole.Coordinator, "c").CanWrite, Is.True);
        Assert.That(new CallerContext("s", "TENANT-A", UserRole.Viewer, "c").CanWrite, Is.False);
    }
}

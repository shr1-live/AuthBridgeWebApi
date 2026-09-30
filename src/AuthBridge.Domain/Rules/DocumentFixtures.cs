namespace AuthBridge.Domain.Rules;

public static class DocumentTypes
{
    public const string ReferralLetter = "ReferralLetter";
    public const string ImagingReport = "ImagingReport";
    public const string TreatmentSummary = "TreatmentSummary";

    public static readonly IReadOnlyList<string> All = [ReferralLetter, ImagingReport, TreatmentSummary];

    public static bool IsKnown(string documentType) => All.Contains(documentType, StringComparer.Ordinal);
}

public sealed record DocumentFixture(string Key, string DocumentType, bool IsValid, string Description);

/// <summary>
/// Allowlisted synthetic document metadata. These are not uploaded files and contain no
/// medical content; attaching one records only its key, type and validity.
/// </summary>
public static class DocumentFixtureCatalog
{
    public static readonly IReadOnlyList<DocumentFixture> All =
    [
        new("FX-REFERRAL-SIGNED", DocumentTypes.ReferralLetter, true, "Signed synthetic referral letter"),
        new("FX-REFERRAL-UNSIGNED", DocumentTypes.ReferralLetter, false, "Synthetic referral letter missing a signature"),
        new("FX-IMAGING-CURRENT", DocumentTypes.ImagingReport, true, "Synthetic imaging report dated within policy window"),
        new("FX-IMAGING-EXPIRED", DocumentTypes.ImagingReport, false, "Synthetic imaging report older than policy window"),
        new("FX-TREATMENT-COMPLETE", DocumentTypes.TreatmentSummary, true, "Complete synthetic treatment summary"),
        new("FX-TREATMENT-INCOMPLETE", DocumentTypes.TreatmentSummary, false, "Synthetic treatment summary with missing sections"),
    ];

    public static DocumentFixture? Find(string fixtureKey) =>
        All.FirstOrDefault(f => string.Equals(f.Key, fixtureKey, StringComparison.Ordinal));
}

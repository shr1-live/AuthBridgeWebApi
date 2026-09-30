namespace AuthBridge.Domain.Rules;

public sealed record PresentDocument(string DocumentType, string FixtureKey, bool IsValid);

public sealed record CompletenessResult(
    IReadOnlyList<string> Required,
    IReadOnlyList<string> Present,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Invalid)
{
    public bool IsComplete => Missing.Count == 0 && Invalid.Count == 0;
}

/// <summary>Pure comparison of required document types against attached documents.</summary>
public static class CompletenessEvaluator
{
    public static CompletenessResult Evaluate(IEnumerable<string> requiredTypes, IEnumerable<PresentDocument> documents)
    {
        var required = requiredTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var byType = documents
            .GroupBy(d => d.DocumentType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        var present = new List<string>();
        var missing = new List<string>();
        var invalid = new List<string>();

        foreach (var type in required)
        {
            if (!byType.TryGetValue(type, out var doc))
                missing.Add(type);
            else if (!doc.IsValid)
                invalid.Add(type);
            else
                present.Add(type);
        }

        return new CompletenessResult(required, present, missing, invalid);
    }
}

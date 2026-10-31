using LeadGen.Core.Ai;

namespace LeadGen.Core.Scoring;

public record ScoreFactor(string Factor, int Score, int Weight);

public record ScoreResult(int Total, string Priority, List<ScoreFactor> Factors);

/// <summary>
/// Deterministic 100-point scoring per SRS 4.3. The model classifies; these rules score.
/// </summary>
public static class LeadScorer
{
    public const string RulesVersion = "score-1.0";

    public static ScoreResult Score(AiResult ai, DateTimeOffset publishedAt, ISet<string> myCategories,
        ISet<string> targetCountries, DateTimeOffset? now = null)
    {
        now ??= DateTimeOffset.UtcNow;
        var factors = new List<ScoreFactor>();

        // 1. Clarity of the service request (30)
        int clarity = ai.IntentStrength switch { "high" => 30, "medium" => 20, "low" => 8, _ => 0 };
        factors.Add(new("clarity_of_request", clarity, 30));

        // 2. Current contracting intent (25)
        int intent = ai.IntentType switch
        {
            "request_for_quotation" or "outsourcing_request" => 25,
            "looking_for_developer" or "vendor_recommendation" => 22,
            _ => 10
        };
        if (ai.IntentStrength == "medium") intent = Math.Min(intent, 15);
        if (ai.IntentStrength == "low") intent = Math.Min(intent, 8);
        factors.Add(new("contracting_intent", intent, 25));

        // 3. Fit with the services I offer (20); with none configured, give half credit.
        int fit = myCategories.Count == 0 ? 10
            : (ai.ServiceCategory != null && myCategories.Contains(ai.ServiceCategory) ? 20 : 0);
        factors.Add(new("service_fit", fit, 20));

        // 4. Project details present (10)
        var d = ai.ServiceDetails;
        int details = 0;
        if (d.Requirements.Count >= 3) details += 4; else if (d.Requirements.Count > 0) details += 2;
        if (!string.IsNullOrEmpty(d.Industry)) details += 2;
        if (!string.IsNullOrEmpty(d.Technology)) details += 2;
        if (d.Budget is not null) details += 1;
        if (!string.IsNullOrEmpty(d.Deadline)) details += 1;
        factors.Add(new("project_details", Math.Min(details, 10), 10));

        // 5. Geography (10): unknown location earns nothing (SRS 4.3).
        int geo = 0;
        var inTarget = ai.Geography.Country != null &&
            (targetCountries.Count == 0 || targetCountries.Contains(ai.Geography.Country));
        if (inTarget) geo = ai.Geography.Status switch { "known" => 10, "probable" => 6, _ => 0 };
        factors.Add(new("geography", geo, 10));

        // 6. Recency (5): affects priority only; never makes an old post an active request.
        var age = now.Value - publishedAt;
        int recency = age.TotalHours <= 24 ? 5 : age.TotalHours <= 72 ? 4 : age.TotalDays <= 7 ? 2 : 0;
        factors.Add(new("recency", recency, 5));

        int total = Math.Clamp(factors.Sum(f => f.Score), 0, 100);
        string priority = total >= 80 ? "high" : total >= 60 ? "medium" : "low";
        return new ScoreResult(total, priority, factors);
    }
}

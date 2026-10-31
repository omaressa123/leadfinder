using LeadGen.Core.Ai;
using LeadGen.Core.Connectors;
using System.Text.RegularExpressions;

namespace LeadGen.Core.Tests;

/// <summary>
/// In-process, deterministic AI double used by the offline test suite.
/// Mirrors the lead-analyzer outcomes the pipeline tests assert against, without
/// requiring the removed Python AI service or any network call.
/// </summary>
public class FakeAi : IAiClient
{
    private static readonly string[] ServiceWords =
    { "erp", "website", "web app", "web application", "online store", "e-commerce", "mobile app", "ios", "android",
      "crm", "chatbot", "ai agent", "accounting system", "inventory system", "management system", "software",
      "developer", "programmer", "company", "agency", "team", "system", "app", "platform", "portal" };

    private static readonly string[] RequestVerbs =
    { "looking for", "need", "needs", "want", "wants", "require", "requires", "seeking", "searching for",
      "build", "develop", "create", "make", "outsource", "recommend", "rfq", "rfp", "quotation", "quote", "outsourc",
      "عايز", "عايزين", "محتاج", "محتاجين", "نبي", "ابغي", "ابي", "ابغى", "نحتاج", "احتاج", "نبغي", "نبغى", "مطلوب" };

    private static readonly string[] CourseWords = { "course", "courses", "tutorial", "bootcamp", "كورس", "دوره", "تعلم", "اتعلم", "شرح" };
    private static readonly string[] JobSeekerWords = { "looking for a job", "hire me", "open to work", "my cv", "resume", "full-time employee", "full-time .net employee" };
    private static readonly string[] AdWords = { "join our team", "we are hiring", "hire us", "we offer", "we provide", "our services", "available for", "special offer" };
    private static readonly string[] ArabicServiceWords = { "شركة", "مكتب", "مبرمج", "مطور", "فريق", "نظام", "erp", "موقع", "تطبيق", "متجر", "شات بوت", "برمجه" };

    public async Task<(AiResult Result, string RawJson)> AnalyzeAsync(
        string content, IReadOnlyList<ExclusionRuleInput> exclusions, CancellationToken ct)
    {
        var lower = content.ToLowerInvariant();
        var active = (exclusions ?? Array.Empty<ExclusionRuleInput>()).Where(e => e.Active).ToList();
        var customMatches = new List<CustomMatch>();
        bool needsReview = false;
        string? reviewReason = null;
        MatchedRuleRef? matchedRule = null;
        string? exclusionSource = null;

        foreach (var r in active)
        {
            var pat = r.Pattern.ToLowerInvariant();
            bool hit = r.MatchType == "keyword" ? Words(lower).Contains(pat) : lower.Contains(pat);
            if (!hit) continue;
            bool negated = Negated(lower, pat);
            var outcome = negated ? "ignored_negated_context" : null;
            customMatches.Add(new CustomMatch(r.Id, r.Pattern, r.MatchType, negated, outcome));
            if (negated) continue;
            if (IsClearRequest(lower))
            {
                needsReview = true;
                reviewReason = $"custom_exclusion_conflict: '{r.Pattern}' appears in a clear purchase request";
                matchedRule = new MatchedRuleRef(r.Id, r.Pattern, r.MatchType);
                exclusionSource = "custom";
                // keep scanning so every custom match is recorded
            }
            else
            {
                return (MakeExcluded("custom_rule", "custom", matchedRule: new MatchedRuleRef(r.Id, r.Pattern, r.MatchType),
                    customMatches: customMatches, customRulesApplied: active.Count), RawJson(false, "custom_rule"));
            }
        }

        var builtIn = BuiltIn(lower);
        if (builtIn is { } b)
        {
            var finalSource = exclusionSource ?? (b.ExclusionReason is null ? null : "builtin");
            return (MakeLead(b.IsLead, b.IntentType, b.IntentStrength, b.ServiceCategory, b.ServiceDetails,
                b.Geography, b.Evidence, b.ExclusionReason, finalSource, matchedRule, needsReview, reviewReason,
                customMatches, active.Count), RawJson(b.IsLead, b.ExclusionReason));
        }

        return (MakeNotLead("no_clear_request", customMatches, active.Count,
            needsReview, reviewReason, matchedRule, exclusionSource), RawJson(false, "no_clear_request"));
    }

    private static bool IsClearRequest(string lower)
    {
        var hasAr = Regex.IsMatch(lower, @"[\u0600-\u06FF]");
        foreach (var v in RequestVerbs)
        {
            var i = lower.IndexOf(v);
            if (i < 0) continue;
            var after = lower[(i + v.Length)..(Math.Min(i + v.Length + 45, lower.Length))];
            if (ServiceWords.Any(s => after.Contains(s))) return true;
            if (hasAr && ArabicServiceWords.Any(s => after.Contains(s))) return true;
        }
        // Arabic request: a request verb anywhere plus a service word anywhere in the text.
        if (hasAr && RequestVerbs.Any(lower.Contains) && ArabicServiceWords.Any(lower.Contains)) return true;
        return false;
    }

    private static (bool IsLead, string IntentType, string IntentStrength, string? ServiceCategory,
        AiDetails ServiceDetails, AiGeo Geography, List<string> Evidence, string? ExclusionReason)? BuiltIn(string lower)
    {
        if (CourseWords.Any(lower.Contains) && !IsClearRequest(lower))
            return (false, "excluded", "none", null, EmptyDetails(), Geo(lower), new(), "course");

        if ((lower.Contains("full-time") && lower.Contains("employee")) || lower.Contains("looking for a job")
            || lower.Contains("hire me") || lower.Contains("open to work") || lower.Contains("my cv"))
            return (false, "excluded", "none", null, EmptyDetails(), Geo(lower), new(), "job_seeker");

        if (AdWords.Any(lower.Contains))
            return (false, "excluded", "none", null, EmptyDetails(), Geo(lower), new(), "advertisement");

        if (!IsClearRequest(lower)) return null;

        string category;
        if (lower.Contains("erp")) category = "ERP_DEVELOPMENT";
        else if (lower.Contains("crm")) category = "CRM_DEVELOPMENT";
        else if (lower.Contains("online store") || lower.Contains("e-commerce")) category = "ECOMMERCE";
        else if (lower.Contains("mobile app") || lower.Contains("ios") || lower.Contains("android")) category = "MOBILE_DEVELOPMENT";
        else if (lower.Contains("website") || lower.Contains("web app") || lower.Contains("portal")) category = "WEB_DEVELOPMENT";
        else if (lower.Contains("chatbot") || lower.Contains("ai agent") || lower.Contains("artificial intelligence")) category = "AI_DEVELOPMENT";
        else if (lower.Contains("api") || lower.Contains("integration") || lower.Contains("webhook")) category = "API_INTEGRATION";
        else if (lower.Contains("desktop") || lower.Contains("windows")) category = "DESKTOP_DEVELOPMENT";
        else if (lower.Contains("maintenance") || lower.Contains("fix") || lower.Contains("support existing")) category = "MAINTENANCE";
        else category = "CUSTOM_SOFTWARE";

        var intent = lower.Contains("quotation") || lower.Contains("rfq") || lower.Contains("quote") || lower.Contains("display") ? "request_for_quotation"
            : lower.Contains("recommend") ? "vendor_recommendation"
            : lower.Contains("outsourc") ? "outsourcing_request"
            : "looking_for_developer";
        var strength = RequestVerbs.Count(v => lower.Contains(v)) >= 2 || ServiceWords.Count(s => lower.Contains(s)) >= 2 ? "high" : "medium";
        var geo = Geo(lower);
        var evidence = ServiceWords.Where(s => lower.Contains(s)).Take(4).ToList();
        return (true, intent, strength, category, EmptyDetails(), geo, evidence, null);
    }

    private static AiGeo Geo(string lower)
    {
        string? country = null, city = null;
        foreach (var (c, pats) in new (string Country, string[] Patterns)[]
        {
            ("Saudi Arabia", new[] { "riyadh", "jeddah", "dammam", "khobar", "mecca", "makkah" }),
            ("Egypt", new[] { "cairo", "giza", "alexandria", "mansoura" }),
            ("UAE", new[] { "dubai", "abu dhabi", "sharjah" }),
            ("Qatar", new[] { "doha" }),
            ("Oman", new[] { "muscat" }),
            ("Kuwait", new[] { "kuwait" }),
            ("Bahrain", new[] { "manama", "المنامه" }),
        })
        {
            if (pats.Any(p => lower.Contains(p))) { country = c; break; }
        }
        foreach (var (c, cityName) in new (string City, string Name)[]
        {
            ("riyadh", "Riyadh"), ("cairo", "Cairo"), ("giza", "Giza"), ("alexandria", "Alexandria"), ("mansoura", "Mansoura"),
            ("jeddah", "Jeddah"), ("dammam", "Dammam"), ("khobar", "Khobar"), ("mecca", "Mecca"), ("makkah", "Mecca"),
            ("dubai", "Dubai"), ("abu dhabi", "Abu Dhabi"), ("sharjah", "Sharjah"), ("doha", "Doha"), ("muscat", "Muscat"),
            ("kuwait", "Kuwait City"), ("manama", "Manama")
        })
        { if (lower.Contains(c)) { city = cityName; break; } }

        var status = country is null ? "unknown" : (city is null ? "probable" : "known");
        var confidence = status == "known" ? 0.9 : (status == "probable" ? 0.7 : 0.0);
        return new AiGeo(country, city, confidence, status);
    }

    private static bool Negated(string lower, string pat)
    {
        return lower.Contains($" not {pat}") || lower.Contains($"not a {pat}") || lower.Contains($"not the {pat}")
            || lower.Contains($"مش {pat}") || lower.Contains($"ما {pat}");
    }

    private static HashSet<string> Words(string lower) => new(lower.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static AiDetails EmptyDetails() => new(null, null, null, new List<string>(), null, null);

    private static string DetectLanguage(string lower)
    {
        var ar = Regex.Matches(lower, @"[\u0600-\u06FF]").Count;
        var en = Regex.Matches(lower, @"[A-Za-z]").Count;
        if (ar > 0 && en > 0 && Math.Min(ar, en) / (double)Math.Max(ar, en) > 0.15) return "mixed";
        return ar > en ? "ar" : "en";
    }

    private static string RawJson(bool isLead, string? exclusionReason) =>
        $"{{\"is_lead\":{isLead.ToString().ToLower()},\"exclusion_reason\":{(exclusionReason is null ? "null" : "\"" + exclusionReason.Replace("\"", "\\\"") + "\"")}}}";

    private static AiResult MakeLead(bool isLead, string intent, string strength, string? category,
        AiDetails details, AiGeo geo, List<string> evidence, string? exclusionReason, string? exclusionSource,
        MatchedRuleRef? matchedRule, bool needsReview, string? reviewReason, List<CustomMatch> customMatches, int customRulesApplied)
    {
        return new AiResult(isLead, intent, strength, category, details, geo, evidence, exclusionReason,
            DetectLanguage(string.Join(" ", evidence)), "fake-1.0", "fake-rules-1",
            needsReview, reviewReason, exclusionSource, matchedRule, customRulesApplied, customMatches, null);
    }

    private static AiResult MakeExcluded(string exclusionReason, string exclusionSource, MatchedRuleRef? matchedRule,
        List<CustomMatch> customMatches, int customRulesApplied)
    {
        return new AiResult(false, "excluded", "none", null, EmptyDetails(), new AiGeo(null, null, 0, "unknown"),
            new(), exclusionReason, "en", "fake-1.0", "fake-rules-1",
            false, null, exclusionSource, matchedRule, customRulesApplied, customMatches, null);
    }

    private static AiResult MakeNotLead(string exclusionReason, List<CustomMatch> customMatches, int customRulesApplied,
        bool needsReview, string? reviewReason, MatchedRuleRef? matchedRule, string? exclusionSource)
    {
        return new AiResult(false, "none", "none", null, EmptyDetails(), new AiGeo(null, null, 0, "unknown"),
            new(), exclusionReason, "en", "fake-1.0", "fake-rules-1",
            needsReview, reviewReason, exclusionSource, matchedRule, customRulesApplied, customMatches, null);
    }
}

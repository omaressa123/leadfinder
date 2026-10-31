using System.Text.Json;

namespace LeadGen.Core.Ai;

/// <summary>Analyzes a post's content and returns a structured lead verdict.</summary>
public interface IAiClient
{
    Task<(AiResult Result, string RawJson)> AnalyzeAsync(
        string content, IReadOnlyList<ExclusionRuleInput> exclusions, CancellationToken ct);
}

/// <summary>A custom exclusion rule sent to the analyzer. MatchType is "phrase" or "keyword".</summary>
public record ExclusionRuleInput(string? Id, string Pattern, string MatchType, bool Active);

public record MatchedRuleRef(string? Id, string Pattern, string MatchType);

public record CustomMatch(string? Id, string Pattern, string MatchType, bool Negated, string? Outcome);

public record AiGeo(string? Country, string? City, double Confidence, string Status);

public record AiDetails(string? Industry, string? Platform, string? Technology,
    List<string> Requirements, JsonElement? Budget, string? Deadline);

public record AiResult(
    bool IsLead, string IntentType, string IntentStrength, string? ServiceCategory,
    AiDetails ServiceDetails, AiGeo Geography, List<string> Evidence, string? ExclusionReason,
    string Language, string ModelVersion, string RulesVersion,
    bool NeedsReview, string? ReviewReason, string? ExclusionSource, MatchedRuleRef? MatchedRule,
    int CustomRulesApplied, List<CustomMatch>? CustomMatches, string? BaseReason);

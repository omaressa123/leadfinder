using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeadGen.Core.Ai;

/// <summary>Configuration for the Google Gemini direct REST integration.</summary>
public record GeminiOptions(string? ApiKey, string Model = "gemini-2.5-flash-lite");

/// <summary>
/// Calls the Google Gemini REST API (generateContent) directly through HttpClient.
/// No Python middle-man: the API key stays in the backend and is never sent to the frontend.
/// The model is instructed to return a single JSON object matching <see cref="AiResult"/>
/// (snake_case), so it drops straight into the existing pipeline and scoring.
/// </summary>
public class GeminiClient(HttpClient http, GeminiOptions options) : IAiClient
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private const string ModelVersion = "gemini-1.0";
    private const string RulesVersion = "2026-10-30.1";

    public async Task<(AiResult Result, string RawJson)> AnalyzeAsync(
        string content, IReadOnlyList<ExclusionRuleInput> exclusions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new HttpRequestException("Gemini API key is not configured (GEMINI_API_KEY).");

        var url = $"v1beta/models/{options.Model}:generateContent?key={Uri.EscapeDataString(options.ApiKey)}";

        var body = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = BuildPrompt(content, exclusions) } } }
            },
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = 0.2,
                ["maxOutputTokens"] = 1200,
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = Schema()
            }
        };

        HttpResponseMessage resp;
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json) };
            resp = await http.SendAsync(msg, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { throw new HttpRequestException("Gemini request timed out"); }
        catch (HttpRequestException) { throw; }
        catch (Exception ex)
        { throw new HttpRequestException($"Gemini unreachable: {ex.Message}", ex); }

        using (resp)
        {
            var raw = await resp.Content.ReadAsStringAsync(ct);

            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                throw new HttpRequestException($"Gemini rejected the API key ({(int)resp.StatusCode}); check GEMINI_API_KEY. {Summarize(raw)}");
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var reset = ResetFrom(resp);
                throw new HttpRequestException($"Gemini rate limit reached{(reset is { } r ? $", retry after {r:u}" : "")}. {Summarize(raw)}");
            }
            if ((int)resp.StatusCode >= 500)
                throw new HttpRequestException($"Gemini API error {(int)resp.StatusCode}. {Summarize(raw)}");
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Gemini API returned {(int)resp.StatusCode}. {Summarize(raw)}");

            AiResult result;
            try
            {
                var text = ExtractText(raw) ?? "{}";
                result = JsonSerializer.Deserialize<AiResult>(text, Json)
                    ?? throw new InvalidOperationException("Gemini returned an empty analysis");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Gemini returned unreadable JSON: {ex.Message}. Raw: {Summarize(raw)}", ex);
            }

            result = result with { ModelVersion = ModelVersion, RulesVersion = RulesVersion };
            return (result, raw);
        }
    }

    private static string BuildPrompt(string content, IReadOnlyList<ExclusionRuleInput> exclusions)
    {
        var exclusionsText = exclusions is { Count: > 0 }
            ? string.Join("\n", exclusions.Select(e => $"- {(e.Active ? "" : "[disabled] ")}pattern=\"{e.Pattern}\" match_type={e.MatchType} active={e.Active}"))
            : "(none)";

        return $"""
You are a lead-analysis assistant. Analyze the following X (Twitter) post and return ONLY a single JSON object (no markdown fences, no extra text).

Post content:
"""
 + content + """
Custom exclusion rules (apply these first; a word inside a clear purchase request is a conflict, keep the lead and flag it for review):
"""
 + exclusionsText + """

Return JSON with exactly these snake_case fields:
{{
  "is_lead": bool,
  "intent_type": "looking_for_developer" | "vendor_recommendation" | "outsourcing_request" | "request_for_quotation" | "none" | "excluded",
  "intent_strength": "high" | "medium" | "low" | "none",
  "service_category": one of WEB_DEVELOPMENT, ERP_DEVELOPMENT, MOBILE_DEVELOPMENT, ECOMMERCE, CRM_DEVELOPMENT, AI_DEVELOPMENT, API_INTEGRATION, DESKTOP_DEVELOPMENT, MAINTENANCE, CUSTOM_SOFTWARE, or null,
  "service_details": {{"industry": string|null, "platform": string|null, "technology": string|null, "requirements": [string], "budget": {{"amount": string|null, "currency": string|null}}|null, "deadline": string|null}},
  "geography": {{"country": string|null, "city": string|null, "confidence": number, "status": "known"|"probable"|"unknown"}},
  "evidence": [string],
  "exclusion_reason": string|null,
  "language": "ar"|"en"|"mixed",
  "model_version": string,
  "rules_version": string,
  "needs_review": bool,
  "review_reason": string|null,
  "exclusion_source": "builtin"|"custom"|null,
  "matched_rule": {{"id": string|null, "pattern": string|null, "match_type": string|null}}|null,
  "custom_rules_applied": int,
  "custom_matches": [{{"id": string|null, "pattern": string|null, "match_type": string|null, "negated": bool, "outcome": string|null}}],
  "base_reason": string|null
}}

Classify the post as a genuine lead only when someone is asking to hire/build software. Exclude job-seekers, courses, ads, showcases, pure questions. For geography, only count explicit country/city names (Arabic alone is not location evidence). Output ONLY the JSON object.
""";
    }

    private static JsonObject Schema()
    {
        // Light schema to nudge Gemini toward the exact shape; the runtime still validates by deserialization.
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["is_lead"] = new JsonObject { ["type"] = "boolean" },
                ["intent_type"] = new JsonObject { ["type"] = "string" },
                ["intent_strength"] = new JsonObject { ["type"] = "string" },
                ["service_category"] = new JsonObject { ["type"] = "string" },
                ["service_details"] = new JsonObject { ["type"] = "object" },
                ["geography"] = new JsonObject { ["type"] = "object" },
                ["evidence"] = new JsonObject { ["type"] = "array" },
                ["exclusion_reason"] = new JsonObject { ["type"] = "string" },
                ["language"] = new JsonObject { ["type"] = "string" },
                ["model_version"] = new JsonObject { ["type"] = "string" },
                ["rules_version"] = new JsonObject { ["type"] = "string" },
                ["needs_review"] = new JsonObject { ["type"] = "boolean" },
                ["review_reason"] = new JsonObject { ["type"] = "string" },
                ["exclusion_source"] = new JsonObject { ["type"] = "string" },
                ["matched_rule"] = new JsonObject { ["type"] = "object" },
                ["custom_rules_applied"] = new JsonObject { ["type"] = "integer" },
                ["custom_matches"] = new JsonObject { ["type"] = "array" },
                ["base_reason"] = new JsonObject { ["type"] = "string" }
            },
            ["required"] = new JsonArray { "is_lead", "intent_type", "intent_strength", "needs_review" }
        };
    }

    private static string? ExtractText(string raw)
    {
        try
        {
            var doc = JsonNode.Parse(raw);
            return doc?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();
        }
        catch { return null; }
    }

    private static string Summarize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        return raw.Length > 300 ? raw[..300] + "…" : raw;
    }

    private static DateTimeOffset? ResetFrom(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("retry-after", out var v) && double.TryParse(v.FirstOrDefault(), out var sec))
            return DateTimeOffset.UtcNow.AddSeconds(sec);
        return null;
    }
}

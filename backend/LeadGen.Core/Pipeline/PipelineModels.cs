using System.Security.Cryptography;
using System.Text;
using LeadGen.Core.Ai;
using LeadGen.Core.Connectors;
using LeadGen.Core.Scoring;

namespace LeadGen.Core.Pipeline;

/// <summary>Everything the pipeline needs to know about a search profile for one run.</summary>
public record ProfileSnapshot(
    Guid Id, Guid UserId, string Name,
    IReadOnlyList<string> Keywords, IReadOnlyList<string> Languages, IReadOnlyList<string> Countries,
    int MinScore, IReadOnlyList<ExclusionRuleInput> Exclusions, ISet<string> MyCategories);

public record SourceState(Guid Id, string SourceType, bool Enabled, DateTimeOffset? RateLimitedUntil, DateTimeOffset? LastFetchAt);

/// <summary>One collected post with its analysis and, when it qualifies, the score that creates a lead.</summary>
public record PostToSave(Guid ProfileId, Guid SourceId, SourcePost Post, string ContentHash,
    AiResult Analysis, string AnalysisJson, ScoreResult? LeadScore);

/// <summary>Thrown by the store when the unique (source, external id) or one-lead-per-post rule is hit.</summary>
public class DuplicatePostException(string message) : Exception(message);

public interface IPipelineStore
{
    Task<ProfileSnapshot?> LoadProfileAsync(Guid profileId, CancellationToken ct);
    Task<IReadOnlyList<SourceState>> GetEnabledSourcesAsync(CancellationToken ct);
    Task<bool> SignalExistsAsync(Guid sourceId, string externalId, string contentHash, CancellationToken ct);
    /// <summary>Saves signal + analysis (+ lead when LeadScore is set) atomically. Throws DuplicatePostException.</summary>
    Task SavePostAsync(PostToSave post, CancellationToken ct);
    Task<string?> GetCursorAsync(Guid profileId, Guid sourceId, CancellationToken ct);
    Task SetCursorAsync(Guid profileId, Guid sourceId, string cursor, CancellationToken ct);
    Task<Guid> StartJobAsync(Guid profileId, Guid sourceId, CancellationToken ct);
    Task FinishJobAsync(Guid jobId, string status, int newPosts, string? error, CancellationToken ct);
    Task UpdateSourceStateAsync(Guid sourceId, DateTimeOffset? rateLimitedUntil, string? lastError,
        DateTimeOffset fetchedAt, CancellationToken ct);
}

public static class ContentHasher
{
    /// <summary>Hash of whitespace/case-normalised text, so identical reposts collapse into one signal.</summary>
    public static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join(' ', content.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))));
}

public enum SourceRunStatus { Done, Failed, Skipped }

public record SourceRunResult(Guid SourceId, string SourceType, SourceRunStatus Status, int NewPosts, int Leads,
    int Excluded, int NeedsReview, string? Error, bool Retriable);

namespace LeadGen.Core.Connectors;

/// <param name="SinceCursor">Opaque per-profile cursor from the last successful fetch (e.g. newest post id), so old posts are not fetched again.</param>
public record SourceSearchRequest(IReadOnlyList<string> Keywords, IReadOnlyList<string> Languages,
    int MaxResults = 50, string? SinceCursor = null);

public record SourcePost(string ExternalId, string Content, string Url, DateTimeOffset PublishedAt);

public enum FetchErrorKind
{
    None,
    NotConfigured,  // no credentials: not retried, user must fix settings
    Auth,           // 401/403: bad token or missing permission; not retried
    RateLimited,    // 429: wait until RateLimitResetsAt, never hammer the API
    Transient       // network failure / 5xx: safe to retry with backoff
}

public record SourceFetchResult(IReadOnlyList<SourcePost> Posts, FetchErrorKind Kind = FetchErrorKind.None,
    string? Error = null, DateTimeOffset? RateLimitResetsAt = null, string? NextCursor = null)
{
    public static SourceFetchResult Fail(FetchErrorKind kind, string error, DateTimeOffset? resetsAt = null) =>
        new(Array.Empty<SourcePost>(), kind, error, resetsAt);
}

/// <summary>
/// One connector per source. Connectors use official, permitted APIs only, respect rate limits,
/// and return a unified post model. They never contain AI or scoring logic.
/// Connectors report failures through the result (and may throw on unexpected errors); the pipeline isolates them.
/// </summary>
public interface ILeadSourceConnector
{
    string SourceName { get; }

    Task<SourceFetchResult> FetchAsync(SourceSearchRequest request, CancellationToken cancellationToken);
}

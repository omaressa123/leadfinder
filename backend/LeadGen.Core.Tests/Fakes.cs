using LeadGen.Core.Ai;
using LeadGen.Core.Connectors;
using LeadGen.Core.Pipeline;

namespace LeadGen.Core.Tests;

public class FakeConnector(string name, Func<SourceSearchRequest, SourceFetchResult> handler) : ILeadSourceConnector
{
    public string SourceName => name;
    public int Calls;
    public SourceSearchRequest? LastRequest;

    public Task<SourceFetchResult> FetchAsync(SourceSearchRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        LastRequest = request;
        return Task.FromResult(handler(request));
    }
}

public class ThrowingConnector(string name) : ILeadSourceConnector
{
    public string SourceName => name;
    public Task<SourceFetchResult> FetchAsync(SourceSearchRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("connector exploded");
}

public class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
    public void Set(DateTimeOffset t) => _now = t;
}

/// <summary>In-memory store that enforces the same uniqueness rules as the database schema.</summary>
public class InMemoryStore : IPipelineStore
{
    public record StoredLead(Guid SignalId, Guid SourceId, string ExternalId, int Score, string Priority, bool NeedsReview, string? ReviewReason);
    public record StoredAnalysis(Guid SourceId, string ExternalId, AiResult Result, string Raw);
    public record Job(Guid Id, Guid ProfileId, Guid SourceId, string Status, int NewPosts, string? Error);

    private readonly object _gate = new();
    public readonly List<SourceState> Sources = new();
    public readonly Dictionary<(Guid, string), (Guid SignalId, string Hash, string Content, string Url, DateTimeOffset Published)> Signals = new();
    public readonly List<StoredAnalysis> Analyses = new();
    public readonly List<StoredLead> Leads = new();
    public readonly Dictionary<(Guid, Guid), string> Cursors = new();
    public readonly Dictionary<Guid, Job> Jobs = new();
    public ProfileSnapshot? Profile;
    public readonly Dictionary<Guid, ProfileSnapshot> Profiles = new();
    public Guid? ThrowOnLoadFor;

    public Task<ProfileSnapshot?> LoadProfileAsync(Guid profileId, CancellationToken ct)
    {
        if (ThrowOnLoadFor == profileId) throw new InvalidOperationException("database exploded");
        return Task.FromResult(Profiles.TryGetValue(profileId, out var p) ? p : Profile);
    }

    public Task<IReadOnlyList<SourceState>> GetEnabledSourcesAsync(CancellationToken ct)
    { lock (_gate) return Task.FromResult<IReadOnlyList<SourceState>>(Sources.Where(s => s.Enabled).ToList()); }

    public Task<bool> SignalExistsAsync(Guid sourceId, string externalId, string contentHash, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(Signals.ContainsKey((sourceId, externalId)) ||
                                   Signals.Any(s => s.Key.Item1 == sourceId && s.Value.Hash == contentHash));
    }

    public Task SavePostAsync(PostToSave p, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Signals.ContainsKey((p.SourceId, p.Post.ExternalId))) throw new DuplicatePostException("signal exists");
            var signalId = Guid.NewGuid();
            Signals[(p.SourceId, p.Post.ExternalId)] = (signalId, p.ContentHash, p.Post.Content, p.Post.Url, p.Post.PublishedAt);
            Analyses.Add(new StoredAnalysis(p.SourceId, p.Post.ExternalId, p.Analysis, p.AnalysisJson));
            if (p.LeadScore is not null)
            {
                if (Leads.Any(l => l.SignalId == signalId)) throw new DuplicatePostException("lead exists for signal");
                Leads.Add(new StoredLead(signalId, p.SourceId, p.Post.ExternalId, p.LeadScore.Total, p.LeadScore.Priority,
                    p.Analysis.NeedsReview, p.Analysis.ReviewReason));
            }
        }
        return Task.CompletedTask;
    }

    public Task<string?> GetCursorAsync(Guid profileId, Guid sourceId, CancellationToken ct)
    { lock (_gate) return Task.FromResult(Cursors.TryGetValue((profileId, sourceId), out var c) ? c : null); }

    public Task SetCursorAsync(Guid profileId, Guid sourceId, string cursor, CancellationToken ct)
    { lock (_gate) Cursors[(profileId, sourceId)] = cursor; return Task.CompletedTask; }

    public Task<Guid> StartJobAsync(Guid profileId, Guid sourceId, CancellationToken ct)
    {
        lock (_gate)
        {
            var id = Guid.NewGuid();
            Jobs[id] = new Job(id, profileId, sourceId, "running", 0, null);
            return Task.FromResult(id);
        }
    }

    public Task FinishJobAsync(Guid jobId, string status, int newPosts, string? error, CancellationToken ct)
    { lock (_gate) Jobs[jobId] = Jobs[jobId] with { Status = status, NewPosts = newPosts, Error = error }; return Task.CompletedTask; }

    public Task UpdateSourceStateAsync(Guid sourceId, DateTimeOffset? rateLimitedUntil, string? lastError,
        DateTimeOffset fetchedAt, CancellationToken ct)
    {
        lock (_gate)
        {
            var i = Sources.FindIndex(s => s.Id == sourceId);
            Sources[i] = Sources[i] with { RateLimitedUntil = rateLimitedUntil, LastFetchAt = fetchedAt };
        }
        return Task.CompletedTask;
    }
}

public static class Fx
{
    public static readonly string AiUrl = Environment.GetEnvironmentVariable("LEADGEN_AI_URL") ?? "http://127.0.0.1:8765";

    public static IAiClient RealAi()
    {
        return new FakeAi();
    }

    public static ExclusionRuleInput Rule(string pattern, string type = "keyword", bool active = true, string? id = null) =>
        new(id ?? pattern, pattern, type, active);

    public static ProfileSnapshot Profile(IReadOnlyList<ExclusionRuleInput>? rules = null, int minScore = 30, params string[] countries) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "test", new[] { "erp", "need developer" }, new[] { "ar", "en" }, countries,
            minScore, rules ?? Array.Empty<ExclusionRuleInput>(), new HashSet<string>());

    public static SourceSearchRequest Req => new(new[] { "x" }, new[] { "en" });

    public static SourcePost Post(string id, string text, DateTimeOffset? at = null) =>
        new(id, text, $"https://example.test/{id}", at ?? DateTimeOffset.UtcNow);
}

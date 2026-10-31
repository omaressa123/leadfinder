using LeadGen.Api.Domain;
using LeadGen.Core.Ai;
using LeadGen.Core.Pipeline;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LeadGen.Api.Data;

/// <summary>Database adapter for the pipeline. All decisions live in LeadGen.Core; this only reads and writes rows.</summary>
public class EfPipelineStore(AppDb db) : IPipelineStore
{
    public async Task<ProfileSnapshot?> LoadProfileAsync(Guid profileId, CancellationToken ct)
    {
        var p = await db.SearchProfiles.AsNoTracking()
            .Include(x => x.Keywords).Include(x => x.Exclusions)
            .FirstOrDefaultAsync(x => x.Id == profileId, ct);
        if (p is null) return null;

        var categories = await db.Services.Where(s => s.UserId == p.UserId).Select(s => s.Category).ToListAsync(ct);
        return new ProfileSnapshot(
            p.Id, p.UserId, p.Name,
            p.Keywords.Select(k => k.Keyword).ToList(), p.Languages, p.Countries, p.MinScore,
            p.Exclusions.Where(e => e.IsActive).Select(e => new ExclusionRuleInput(e.Id.ToString(), e.Pattern, e.MatchType, true)).ToList(),
            categories.ToHashSet());
    }

    public async Task<IReadOnlyList<SourceState>> GetEnabledSourcesAsync(CancellationToken ct) =>
        await db.SourceConnections.AsNoTracking().Where(s => s.Status == "enabled")
            .Select(s => new SourceState(s.Id, s.SourceType, true, s.RateLimitedUntil, s.LastFetchAt)).ToListAsync(ct);

    public Task<bool> SignalExistsAsync(Guid sourceId, string externalId, string contentHash, CancellationToken ct) =>
        db.RawSignals.AnyAsync(s => s.SourceId == sourceId && (s.ExternalId == externalId || s.ContentHash == contentHash), ct);

    public async Task SavePostAsync(PostToSave p, CancellationToken ct)
    {
        var signal = new RawSignal
        {
            SourceId = p.SourceId, ExternalId = p.Post.ExternalId, Content = p.Post.Content, Url = p.Post.Url,
            PublishedAt = p.Post.PublishedAt.ToUniversalTime(), ContentHash = p.ContentHash
        };
        db.RawSignals.Add(signal);
        db.AiAnalysisResults.Add(new AiAnalysisResult
        {
            SignalId = signal.Id, Intent = p.Analysis.IntentType, Category = p.Analysis.ServiceCategory,
            Confidence = (decimal)p.Analysis.Geography.Confidence, JsonResult = p.AnalysisJson,
            ModelVersion = p.Analysis.ModelVersion, RulesVersion = $"{p.Analysis.RulesVersion}+{LeadGen.Core.Scoring.LeadScorer.RulesVersion}"
        });

        if (p.LeadScore is not null)
        {
            var lead = new Lead
            {
                SignalId = signal.Id, Category = p.Analysis.ServiceCategory, Score = p.LeadScore.Total,
                Priority = p.LeadScore.Priority, NeedsReview = p.Analysis.NeedsReview, ReviewReason = p.Analysis.ReviewReason,
                Country = p.Analysis.Geography.Country, City = p.Analysis.Geography.City
            };
            db.Leads.Add(lead);
            db.LeadScoreFactors.AddRange(p.LeadScore.Factors.Select(f =>
                new LeadScoreFactor { LeadId = lead.Id, Factor = f.Factor, Score = f.Score, Weight = f.Weight }));
            if (p.LeadScore.Priority == "high") db.Notifications.Add(new Notification { LeadId = lead.Id });
        }

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();   // drop the failed inserts so the context stays usable
            throw new DuplicatePostException("post or lead already exists");
        }
    }

    public async Task<string?> GetCursorAsync(Guid profileId, Guid sourceId, CancellationToken ct) =>
        await db.CollectionCursors.AsNoTracking().Where(c => c.ProfileId == profileId && c.SourceId == sourceId)
            .Select(c => c.Cursor).FirstOrDefaultAsync(ct);

    public async Task SetCursorAsync(Guid profileId, Guid sourceId, string cursor, CancellationToken ct)
    {
        var row = await db.CollectionCursors.FirstOrDefaultAsync(c => c.ProfileId == profileId && c.SourceId == sourceId, ct);
        if (row is null) db.CollectionCursors.Add(new CollectionCursor { ProfileId = profileId, SourceId = sourceId, Cursor = cursor });
        else { row.Cursor = cursor; row.UpdatedAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(ct);
    }

    public async Task<Guid> StartJobAsync(Guid profileId, Guid sourceId, CancellationToken ct)
    {
        var job = new CollectionJob { ProfileId = profileId, SourceId = sourceId, Status = "running", StartedAt = DateTimeOffset.UtcNow };
        db.CollectionJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job.Id;
    }

    public async Task FinishJobAsync(Guid jobId, string status, int newPosts, string? error, CancellationToken ct)
    {
        var job = await db.CollectionJobs.FirstAsync(j => j.Id == jobId, ct);
        job.Status = status; job.Fetched = newPosts; job.Error = error; job.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateSourceStateAsync(Guid sourceId, DateTimeOffset? rateLimitedUntil, string? lastError,
        DateTimeOffset fetchedAt, CancellationToken ct)
    {
        var s = await db.SourceConnections.FirstAsync(x => x.Id == sourceId, ct);
        s.RateLimitedUntil = rateLimitedUntil?.ToUniversalTime();
        s.LastError = lastError;
        s.LastFetchAt = fetchedAt.ToUniversalTime();
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

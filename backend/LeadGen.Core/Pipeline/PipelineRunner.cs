using LeadGen.Core.Ai;
using LeadGen.Core.Connectors;
using LeadGen.Core.Scoring;

namespace LeadGen.Core.Pipeline;

public record RunOptions(TimeSpan MinSourceGap, TimeSpan DefaultRateLimitWait)
{
    public static RunOptions Default => new(TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(15));
}

/// <summary>
/// collect -> dedupe -> analyze (custom rules, built-in rules, intent) -> score -> lead.
/// Each source runs in isolation: one failing source never stops the others. Nothing here contacts anyone.
/// </summary>
public class PipelineRunner(IPipelineStore store, IAiClient ai, IEnumerable<ILeadSourceConnector> connectors,
    TimeProvider? clock = null, RunOptions? options = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly RunOptions _options = options ?? RunOptions.Default;

    public async Task<IReadOnlyList<SourceRunResult>> RunProfileAsync(ProfileSnapshot profile, CancellationToken ct)
    {
        var results = new List<SourceRunResult>();
        foreach (var source in await store.GetEnabledSourcesAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await RunSourceAsync(profile, source, ct));
        }
        return results;
    }

    private async Task<SourceRunResult> RunSourceAsync(ProfileSnapshot profile, SourceState source, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        SourceRunResult Skip(string why) => new(source.Id, source.SourceType, SourceRunStatus.Skipped, 0, 0, 0, 0, why, false);

        var connector = connectors.FirstOrDefault(c => c.SourceName == source.SourceType);
        if (connector is null) return Skip("no connector for this source yet");
        if (source.RateLimitedUntil is { } until && until > now) return Skip($"rate limited until {until:u}");
        if (source.LastFetchAt is { } last && now - last < _options.MinSourceGap) return Skip("minimum gap between calls not reached");

        var jobId = await store.StartJobAsync(profile.Id, source.Id, ct);
        int newPosts = 0, leads = 0, excluded = 0, review = 0;

        async Task<SourceRunResult> Fail(string error, bool retriable)
        {
            await store.FinishJobAsync(jobId, "failed", newPosts, error, ct);
            return new(source.Id, source.SourceType, SourceRunStatus.Failed, newPosts, leads, excluded, review, error, retriable);
        }

        try
        {
            var cursor = await store.GetCursorAsync(profile.Id, source.Id, ct);
            var fetched = await connector.FetchAsync(
                new SourceSearchRequest(profile.Keywords, profile.Languages, 50, cursor), ct);

            DateTimeOffset? limitedUntil = fetched.Kind == FetchErrorKind.RateLimited
                ? fetched.RateLimitResetsAt ?? now + _options.DefaultRateLimitWait
                : fetched.Kind == FetchErrorKind.None ? fetched.RateLimitResetsAt   // quota used up: park until the window resets
                : null;
            await store.UpdateSourceStateAsync(source.Id, limitedUntil, fetched.Error, now, ct);

            if (fetched.Kind != FetchErrorKind.None)
                return await Fail(fetched.Error ?? fetched.Kind.ToString(), fetched.Kind == FetchErrorKind.Transient);

            foreach (var post in fetched.Posts.OrderBy(p => p.PublishedAt))
            {
                var hash = ContentHasher.Hash(post.Content);
                if (await store.SignalExistsAsync(source.Id, post.ExternalId, hash, ct)) continue;

                AiResult analysis; string raw;
                try { (analysis, raw) = await ai.AnalyzeAsync(post.Content, profile.Exclusions, ct); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    // Nothing was saved for this post and the cursor is not advanced, so it is retried next run.
                    return await Fail($"AI service unavailable: {ex.Message}", retriable: true);
                }

                ScoreResult? score = null;
                if (analysis.IsLead)
                {
                    var s = LeadScorer.Score(analysis, post.PublishedAt, profile.MyCategories, profile.Countries.ToHashSet(), now);
                    if (s.Total >= profile.MinScore) score = s;
                }

                try { await store.SavePostAsync(new PostToSave(profile.Id, source.Id, post, hash, analysis, raw, score), ct); }
                catch (DuplicatePostException) { continue; }

                newPosts++;
                if (score is not null) { leads++; if (analysis.NeedsReview) review++; }
                else if (!analysis.IsLead) excluded++;
            }

            if (fetched.NextCursor is not null) await store.SetCursorAsync(profile.Id, source.Id, fetched.NextCursor, ct);
            await store.FinishJobAsync(jobId, "done", newPosts, null, ct);
            return new(source.Id, source.SourceType, SourceRunStatus.Done, newPosts, leads, excluded, review, null, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return await Fail(ex.Message, retriable: true);
        }
    }
}

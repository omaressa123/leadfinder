using LeadGen.Api.Domain;
using LeadGen.Core.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace LeadGen.Api.Data;

/// <summary>
/// Database adapter for scheduling. The claim is a single conditional UPDATE, so even with several API instances
/// only one can win it: that is what prevents the same profile from running twice at once.
/// </summary>
public class EfScheduleStore(AppDb db) : IScheduleStore
{
    public static ScheduleSettings ToSettings(SearchProfile p) => new(
        p.ScheduleMode switch { "interval" => ScheduleMode.Interval, "daily" => ScheduleMode.Daily, _ => ScheduleMode.Off },
        p.ScheduleIntervalMinutes, p.ScheduleDailyTime, p.ScheduleTimeZone, p.SchedulePaused);

    public static string ModeName(ScheduleMode m) => m switch { ScheduleMode.Interval => "interval", ScheduleMode.Daily => "daily", _ => "off" };

    private static ScheduledProfile ToModel(SearchProfile p) =>
        new(p.Id, ToSettings(p), p.NextRunAt, p.LastRunAt, p.LastRunStatus, p.RetryAttempts);

    public async Task<ScheduledProfile?> GetAsync(Guid profileId, CancellationToken ct)
    {
        var p = await db.SearchProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == profileId, ct);
        return p is null ? null : ToModel(p);
    }

    public async Task<IReadOnlyList<ScheduledProfile>> GetDueAsync(DateTimeOffset now, CancellationToken ct) =>
        (await db.SearchProfiles.AsNoTracking()
            .Where(p => p.IsActive && p.ScheduleMode != "off" && !p.SchedulePaused && p.NextRunAt != null && p.NextRunAt <= now)
            .ToListAsync(ct)).Select(ToModel).ToList();

    public async Task<string?> TryClaimAsync(Guid profileId, DateTimeOffset now, TimeSpan staleAfter, CancellationToken ct)
    {
        var token = Guid.NewGuid().ToString("N");
        var staleBefore = now - staleAfter;
        var rows = await db.SearchProfiles
            .Where(p => p.Id == profileId && (p.RunningSince == null || p.RunningSince < staleBefore))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.RunningSince, now).SetProperty(p => p.RunToken, token), ct);
        return rows == 1 ? token : null;
    }

    public async Task CompleteRunAsync(Guid profileId, string claimToken, RunCompletion c, CancellationToken ct)
    {
        // Token-guarded: a run that was taken over after going stale cannot overwrite the new run's state.
        var q = db.SearchProfiles.Where(p => p.Id == profileId && p.RunToken == claimToken);
        if (c.UpdateSchedule)
            await q.ExecuteUpdateAsync(s => s
                .SetProperty(p => p.RunningSince, (DateTimeOffset?)null).SetProperty(p => p.RunToken, (string?)null)
                .SetProperty(p => p.LastRunAt, c.FinishedAt).SetProperty(p => p.LastRunStatus, c.Status)
                .SetProperty(p => p.LastRunError, c.Error)
                .SetProperty(p => p.NextRunAt, c.NextRunAt).SetProperty(p => p.RetryAttempts, c.RetryAttempts), ct);
        else
            await q.ExecuteUpdateAsync(s => s
                .SetProperty(p => p.RunningSince, (DateTimeOffset?)null).SetProperty(p => p.RunToken, (string?)null)
                .SetProperty(p => p.LastRunAt, c.FinishedAt).SetProperty(p => p.LastRunStatus, c.Status)
                .SetProperty(p => p.LastRunError, c.Error), ct);
    }
}

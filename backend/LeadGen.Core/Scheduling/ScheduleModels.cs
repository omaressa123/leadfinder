using LeadGen.Core.Pipeline;

namespace LeadGen.Core.Scheduling;

public enum ScheduleMode { Off, Interval, Daily }

public enum RunTrigger { Manual, Scheduled }

/// <summary>
/// How often a search profile runs. "Every 6 hours" is Interval/360, "Daily" is Daily at a time in a time zone,
/// and a custom schedule is Interval with any number of minutes (15 to 7 days).
/// </summary>
public record ScheduleSettings(ScheduleMode Mode, int IntervalMinutes, TimeOnly DailyTime, string TimeZoneId, bool Paused)
{
    public static ScheduleSettings Off => new(ScheduleMode.Off, 360, new TimeOnly(9, 0), "UTC", false);
    public static ScheduleSettings EverySixHours(string tz = "UTC") => new(ScheduleMode.Interval, 360, new TimeOnly(9, 0), tz, false);
    public static ScheduleSettings DailyAt(TimeOnly at, string tz) => new(ScheduleMode.Daily, 360, at, tz, false);
    public static ScheduleSettings Custom(int minutes, string tz = "UTC") => new(ScheduleMode.Interval, minutes, new TimeOnly(9, 0), tz, false);
}

public record ScheduledProfile(Guid ProfileId, ScheduleSettings Settings, DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt, string? LastStatus, int RetryAttempts);

/// <summary>What gets written when a run finishes (or is cancelled): the claim is released and the schedule advances.</summary>
public record RunCompletion(DateTimeOffset FinishedAt, string Status, string? Error, bool UpdateSchedule,
    DateTimeOffset? NextRunAt, int RetryAttempts);

public enum CoordinatedOutcome { Completed, AlreadyRunning, ProfileNotFound }

public record CoordinatedRun(CoordinatedOutcome Outcome, IReadOnlyList<SourceRunResult> Sources, string Status,
    DateTimeOffset? NextRunAt);

public record SchedulerOptions(TimeSpan StaleClaimAfter)
{
    /// <summary>A claim older than this is treated as a crashed run and may be taken over.</summary>
    public static SchedulerOptions Default => new(TimeSpan.FromMinutes(45));
}

public interface IScheduleStore
{
    Task<ScheduledProfile?> GetAsync(Guid profileId, CancellationToken ct);

    /// <summary>Active, un-paused, scheduled profiles whose next run time has passed.</summary>
    Task<IReadOnlyList<ScheduledProfile>> GetDueAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Atomically claims the profile so only one run can be active at a time, across instances.
    /// Returns a token, or null when a live run already holds the claim.
    /// </summary>
    Task<string?> TryClaimAsync(Guid profileId, DateTimeOffset now, TimeSpan staleAfter, CancellationToken ct);

    /// <summary>Releases the claim for this token only (a taken-over stale run must not overwrite the new one).</summary>
    Task CompleteRunAsync(Guid profileId, string claimToken, RunCompletion completion, CancellationToken ct);
}

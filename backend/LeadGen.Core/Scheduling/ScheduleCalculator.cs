namespace LeadGen.Core.Scheduling;

public static class ScheduleCalculator
{
    public const int MinIntervalMinutes = 15;
    public const int MaxIntervalMinutes = 7 * 24 * 60;

    /// <returns>An error message, or null when the settings are valid.</returns>
    public static string? Validate(ScheduleSettings s)
    {
        if (s.Mode == ScheduleMode.Off) return null;
        try { TimeZoneInfo.FindSystemTimeZoneById(s.TimeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { return $"Unknown time zone '{s.TimeZoneId}'"; }
        if (s.Mode == ScheduleMode.Interval && (s.IntervalMinutes < MinIntervalMinutes || s.IntervalMinutes > MaxIntervalMinutes))
            return $"Interval must be between {MinIntervalMinutes} minutes and 7 days";
        return null;
    }

    /// <summary>Next run strictly after <paramref name="after"/>; null when off or paused.</summary>
    public static DateTimeOffset? NextRun(ScheduleSettings s, DateTimeOffset after)
    {
        if (s.Mode == ScheduleMode.Off || s.Paused || Validate(s) is not null) return null;
        return s.Mode == ScheduleMode.Interval
            ? after.AddMinutes(s.IntervalMinutes)
            : NextDaily(s.DailyTime, TimeZoneInfo.FindSystemTimeZoneById(s.TimeZoneId), after);
    }

    private static DateTimeOffset NextDaily(TimeOnly at, TimeZoneInfo tz, DateTimeOffset after)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, tz).DateTime);
        for (var i = 0; i < 4; i++)
        {
            var local = today.AddDays(i).ToDateTime(at);                 // wall-clock time, Kind unspecified
            if (tz.IsInvalidTime(local)) local = local.AddHours(1);      // skipped by a spring-forward change
            var offset = tz.IsAmbiguousTime(local)                       // repeated by a fall-back change: use the first
                ? tz.GetAmbiguousTimeOffsets(local).Max()
                : tz.GetUtcOffset(local);
            var instant = new DateTimeOffset(local, offset);
            if (instant > after) return instant;
        }
        throw new InvalidOperationException("Could not compute the next daily run");
    }
}

public static class RetryPolicy
{
    private static readonly TimeSpan[] Backoff = { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(45) };

    public static int MaxRetries => Backoff.Length;

    /// <param name="consecutiveFailures">Failures in a row including the one that just happened (1 = first failure).</param>
    /// <returns>How long to wait before retrying, or null when retries are used up and the normal schedule resumes.</returns>
    public static TimeSpan? DelayAfter(int consecutiveFailures) =>
        consecutiveFailures >= 1 && consecutiveFailures <= Backoff.Length ? Backoff[consecutiveFailures - 1] : null;
}

/// <summary>Pure rules for what happens to the schedule when the owner edits, pauses or resumes it.</summary>
public static class ScheduleEditor
{
    /// <summary>Saving a schedule sets the first run to the next occurrence. Nothing runs automatically at save time.</summary>
    public static (ScheduleSettings Settings, DateTimeOffset? NextRunAt, int RetryAttempts) Save(ScheduleSettings incoming, DateTimeOffset now) =>
        (incoming, ScheduleCalculator.NextRun(incoming, now), 0);

    public static (ScheduleSettings Settings, DateTimeOffset? NextRunAt) Pause(ScheduleSettings current) =>
        (current with { Paused = true }, null);

    /// <summary>Resuming never replays missed runs: the next run is the next scheduled time after now.</summary>
    public static (ScheduleSettings Settings, DateTimeOffset? NextRunAt) Resume(ScheduleSettings current, DateTimeOffset now)
    {
        var resumed = current with { Paused = false };
        return (resumed, ScheduleCalculator.NextRun(resumed, now));
    }
}

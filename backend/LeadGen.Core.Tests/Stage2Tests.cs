using LeadGen.Core.Connectors;
using LeadGen.Core.Pipeline;
using LeadGen.Core.Scheduling;

namespace LeadGen.Core.Tests;

/// <summary>Stage 2: scheduled runs.</summary>
public static class Stage2Tests
{
    private static readonly TimeSpan H = TimeSpan.FromHours(1);
    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    public static async Task RunAll()
    {
        await T.Run("Stage 2: schedule calculation (interval, daily, time zones, DST, validation)", Calculator);
        await T.Run("Stage 2: runs automatically on schedule, records last/next run", RunsOnSchedule);
        await T.Run("Stage 2: pause and resume", PauseResume);
        await T.Run("Stage 2: the same profile never runs twice at once", NoOverlap);
        await T.Run("Stage 2: retry with backoff, then back to the normal schedule", Retry);
        await T.Run("Stage 2: old posts are not reprocessed", NoReprocessing);
        await T.Run("Stage 2: source rate limits and minimum gap", RateLimits);
        await T.Run("Stage 2: a failing source or profile does not stop the rest", Isolation);
    }

    private static Task Calculator()
    {
        var t = Utc(2026, 10, 30, 8);
        T.Eq("every 6 hours", t.AddHours(6), ScheduleCalculator.NextRun(ScheduleSettings.EverySixHours(), t)!.Value);
        T.Eq("custom 90 minutes", t.AddMinutes(90), ScheduleCalculator.NextRun(ScheduleSettings.Custom(90), t)!.Value);
        T.Check("custom interval below 15 minutes is invalid", ScheduleCalculator.Validate(ScheduleSettings.Custom(5)) is not null);
        T.Check("interval above 7 days is invalid", ScheduleCalculator.Validate(ScheduleSettings.Custom(7 * 24 * 60 + 1)) is not null);
        T.Check("unknown time zone is invalid", ScheduleCalculator.Validate(ScheduleSettings.DailyAt(new TimeOnly(9, 0), "Mars/Base")) is not null);
        T.Check("off has no next run", ScheduleCalculator.NextRun(ScheduleSettings.Off, t) is null);
        T.Check("paused has no next run", ScheduleCalculator.NextRun(ScheduleSettings.EverySixHours() with { Paused = true }, t) is null);

        var cairo = ScheduleSettings.DailyAt(new TimeOnly(9, 0), "Africa/Cairo");
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");
        var next = ScheduleCalculator.NextRun(cairo, Utc(2026, 10, 30, 6))!.Value;
        var local = TimeZoneInfo.ConvertTime(next, tz);
        T.Check("daily 09:00 Cairo lands on 09:00 Cairo time", local.Hour == 9 && local.Minute == 0, local.ToString("o"));
        T.Check("daily run is within the next 24h", next > Utc(2026, 10, 30, 6) && next <= Utc(2026, 10, 31, 6));
        var after = ScheduleCalculator.NextRun(cairo, next)!.Value;
        T.Eq("asked again at exactly run time: moves to the next day", next.AddDays(1), after);

        var ny = ScheduleSettings.DailyAt(new TimeOnly(2, 30), "America/New_York");
        T.Eq("DST spring-forward gap (02:30 does not exist) runs at 03:30 local", Utc(2026, 3, 8, 7, 30),
            ScheduleCalculator.NextRun(ny, Utc(2026, 3, 7, 12))!.Value);
        var ny2 = ScheduleSettings.DailyAt(new TimeOnly(1, 30), "America/New_York");
        T.Eq("DST fall-back repeated hour runs once, at the first 01:30", Utc(2026, 11, 1, 5, 30),
            ScheduleCalculator.NextRun(ny2, Utc(2026, 10, 31, 12))!.Value);
        return Task.CompletedTask;
    }

    private static async Task RunsOnSchedule()
    {
        var conn = new FakeConnector("x", _ => new SourceFetchResult(Array.Empty<SourcePost>()));
        var rig = Rig.Create(new[] { conn });
        var posts = rig.Posts("0", "1", "2");
        var real = new FakeConnector("x", _ => new SourceFetchResult(posts, NextCursor: "2"));
        rig = Rig.Create(new[] { real });
        posts = rig.Posts("0", "1", "2");
        var t0 = rig.Clock.GetUtcNow();

        T.Eq("first run is scheduled 6h after saving", t0 + 6 * H, rig.Row.NextRunAt!.Value);
        T.Eq("not due yet: nothing runs", 0, (await rig.Scheduler.TickAsync(default)).Count);
        T.Eq("connector untouched before the schedule", 0, real.Calls);

        rig.Clock.Advance(6 * H);
        var tick = await rig.Scheduler.TickAsync(default);
        T.Eq("due: exactly one run", 1, tick.Count);
        T.Eq("connector called once", 1, real.Calls);
        T.Check("run completed with status ok", tick[0].Run is { Outcome: CoordinatedOutcome.Completed, Status: "ok" });
        T.Eq("last run recorded", t0 + 6 * H, rig.Row.LastRunAt!.Value);
        T.Eq("next run recorded (+6h)", t0 + 12 * H, rig.Row.NextRunAt!.Value);
        T.Eq("leads created by the scheduled run", 3, rig.Pipe.Leads.Count);

        T.Eq("ticking again straight away does nothing", 0, (await rig.Scheduler.TickAsync(default)).Count);
        rig.Clock.Advance(3 * H);
        T.Eq("3h later: still not due", 0, (await rig.Scheduler.TickAsync(default)).Count);
        rig.Clock.Advance(3 * H);
        T.Eq("6h later: runs again", 1, (await rig.Scheduler.TickAsync(default)).Count);
        T.Eq("no duplicate leads across runs", 3, rig.Pipe.Leads.Count);

        // Server was down for 2 days: one catch-up run, not a burst of missed runs.
        rig.Clock.Advance(TimeSpan.FromDays(2));
        T.Eq("after downtime: a single catch-up run", 1, (await rig.Scheduler.TickAsync(default)).Count);
        T.Eq("then back to normal spacing", rig.Clock.GetUtcNow() + 6 * H, rig.Row.NextRunAt!.Value);
        T.Eq("and nothing more is due", 0, (await rig.Scheduler.TickAsync(default)).Count);

        // A manual run must not move the schedule.
        var nextBefore = rig.Row.NextRunAt;
        rig.Clock.Advance(H);
        var manual = await rig.Coordinator.RunAsync(rig.Profile.Id, RunTrigger.Manual, default);
        T.Check("manual run works", manual.Outcome == CoordinatedOutcome.Completed);
        T.Eq("manual run leaves the next scheduled time alone", nextBefore, rig.Row.NextRunAt);
    }

    private static async Task PauseResume()
    {
        var conn = new FakeConnector("x", _ => new SourceFetchResult(Array.Empty<SourcePost>()));
        var rig = Rig.Create(new[] { conn });

        var (paused, nextWhenPaused) = ScheduleEditor.Pause(rig.Row.Settings);
        rig.Row.Settings = paused; rig.Row.NextRunAt = nextWhenPaused;
        T.Check("paused: next run cleared", rig.Row.NextRunAt is null && rig.Row.Settings.Paused);

        rig.Clock.Advance(TimeSpan.FromHours(30));
        T.Eq("paused: overdue profile does not run", 0, (await rig.Scheduler.TickAsync(default)).Count);
        T.Eq("paused: connector never called", 0, conn.Calls);

        var (resumed, nextAfterResume) = ScheduleEditor.Resume(rig.Row.Settings, rig.Clock.GetUtcNow());
        rig.Row.Settings = resumed; rig.Row.NextRunAt = nextAfterResume;
        T.Eq("resume schedules the next occurrence, no instant burst", rig.Clock.GetUtcNow() + 6 * H, rig.Row.NextRunAt!.Value);
        T.Eq("right after resume nothing runs", 0, (await rig.Scheduler.TickAsync(default)).Count);
        rig.Clock.Advance(6 * H);
        T.Eq("after the interval it runs", 1, (await rig.Scheduler.TickAsync(default)).Count);

        var (saved, savedNext, attempts) = ScheduleEditor.Save(ScheduleSettings.Custom(60), rig.Clock.GetUtcNow());
        T.Check("editing a schedule recomputes the next run and clears retries",
            saved.IntervalMinutes == 60 && savedNext == rig.Clock.GetUtcNow() + H && attempts == 0);
    }

    private static async Task NoOverlap()
    {
        // 1) A run is in progress: manual run, scheduler tick and a second manual run are all refused.
        var gated = new GatedConnector("x", () => new SourceFetchResult(Array.Empty<SourcePost>()));
        var rig = Rig.Create(new[] { gated });
        rig.Clock.Advance(7 * H);   // profile is due

        var first = rig.Coordinator.RunAsync(rig.Profile.Id, RunTrigger.Manual, default);
        await gated.Entered.Task;
        var second = await rig.Coordinator.RunAsync(rig.Profile.Id, RunTrigger.Manual, default);
        T.Eq("second manual run is refused while one is active", CoordinatedOutcome.AlreadyRunning, second.Outcome);
        var tick = await rig.Scheduler.TickAsync(default);
        T.Check("scheduler tick does not start a parallel run", tick.Count == 1 && tick[0].Run?.Outcome == CoordinatedOutcome.AlreadyRunning);
        T.Eq("connector called only once", 1, gated.Calls);
        gated.Gate.SetResult();
        T.Eq("the original run finishes normally", CoordinatedOutcome.Completed, (await first).Outcome);
        T.Eq("claim released: a new run is allowed afterwards", CoordinatedOutcome.Completed,
            (await rig.Coordinator.RunAsync(rig.Profile.Id, RunTrigger.Manual, default)).Outcome);

        // 2) Ten simultaneous attempts: exactly one wins.
        var gated2 = new GatedConnector("x", () => new SourceFetchResult(Array.Empty<SourcePost>()));
        var rig2 = Rig.Create(new[] { gated2 });
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() => rig2.Coordinator.RunAsync(rig2.Profile.Id, RunTrigger.Manual, default))).ToList();
        await gated2.Entered.Task;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (tasks.Count(t => t.IsCompleted) < 9 && DateTime.UtcNow < deadline) await Task.Delay(10);
        gated2.Gate.SetResult();
        var outcomes = (await Task.WhenAll(tasks)).Select(r => r.Outcome).ToList();
        T.Eq("10 simultaneous attempts: one runs", 1, outcomes.Count(o => o == CoordinatedOutcome.Completed));
        T.Eq("10 simultaneous attempts: nine refused", 9, outcomes.Count(o => o == CoordinatedOutcome.AlreadyRunning));
        T.Eq("the source was fetched once", 1, gated2.Calls);

        // 3) A crashed run (claim never released) is taken over after the stale timeout; the old run cannot overwrite it.
        var ok = new FakeConnector("x", _ => new SourceFetchResult(Array.Empty<SourcePost>()));
        var rig3 = Rig.Create(new[] { ok });
        rig3.Row.RunningSince = rig3.Clock.GetUtcNow() - TimeSpan.FromMinutes(10); rig3.Row.Token = "dead";
        T.Eq("fresh claim (10 min old) still blocks", CoordinatedOutcome.AlreadyRunning,
            (await rig3.Coordinator.RunAsync(rig3.Profile.Id, RunTrigger.Manual, default)).Outcome);
        rig3.Row.RunningSince = rig3.Clock.GetUtcNow() - TimeSpan.FromMinutes(50);
        T.Eq("stale claim (50 min old) is taken over", CoordinatedOutcome.Completed,
            (await rig3.Coordinator.RunAsync(rig3.Profile.Id, RunTrigger.Manual, default)).Outcome);
        await rig3.Sched.CompleteRunAsync(rig3.Profile.Id, "dead", new(rig3.Clock.GetUtcNow(), "zombie", null, true, null, 9), default);
        T.Check("late completion from the dead run is ignored", rig3.Row.LastStatus == "ok" && rig3.Row.RetryAttempts == 0);
    }

    private static async Task Retry()
    {
        int calls = 0;
        var flaky = new FakeConnector("x", _ => { calls++; return SourceFetchResult.Fail(FetchErrorKind.Transient, "network down"); });
        var rig = Rig.Create(new[] { flaky });
        rig.Clock.Advance(6 * H);

        async Task<string> Tick() => (await rig.Scheduler.TickAsync(default)).Single().Run!.Status;
        var now = rig.Clock.GetUtcNow();

        T.Eq("failure 1: retrying", "retrying", await Tick());
        T.Eq("retry in 5 minutes", now + TimeSpan.FromMinutes(5), rig.Row.NextRunAt!.Value);
        T.Eq("attempt counter", 1, rig.Row.RetryAttempts);
        T.Eq("not retried early", 0, (await rig.Scheduler.TickAsync(default)).Count);

        rig.Clock.Advance(TimeSpan.FromMinutes(5)); now = rig.Clock.GetUtcNow();
        T.Eq("failure 2: retrying", "retrying", await Tick());
        T.Eq("retry in 15 minutes", now + TimeSpan.FromMinutes(15), rig.Row.NextRunAt!.Value);

        rig.Clock.Advance(TimeSpan.FromMinutes(15)); now = rig.Clock.GetUtcNow();
        T.Eq("failure 3: retrying", "retrying", await Tick());
        T.Eq("retry in 45 minutes", now + TimeSpan.FromMinutes(45), rig.Row.NextRunAt!.Value);

        rig.Clock.Advance(TimeSpan.FromMinutes(45)); now = rig.Clock.GetUtcNow();
        T.Eq("failure 4: retries used up", "failed", await Tick());
        T.Eq("back to the normal 6h schedule", now + 6 * H, rig.Row.NextRunAt!.Value);
        T.Eq("attempt counter reset", 0, rig.Row.RetryAttempts);
        T.Eq("the error is recorded", "network down", rig.Row.LastError);
        T.Eq("connector was tried 4 times, no hammering", 4, calls);

        // Recovers after one failure.
        int n = 0;
        var recovers = new FakeConnector("x", _ => n++ == 0
            ? SourceFetchResult.Fail(FetchErrorKind.Transient, "blip")
            : new SourceFetchResult(Array.Empty<SourcePost>()));
        var rig2 = Rig.Create(new[] { recovers });
        rig2.Clock.Advance(6 * H);
        await rig2.Scheduler.TickAsync(default);
        T.Eq("after a blip: retry scheduled", 1, rig2.Row.RetryAttempts);
        rig2.Clock.Advance(TimeSpan.FromMinutes(5));
        var ok = (await rig2.Scheduler.TickAsync(default)).Single().Run!;
        T.Check("retry succeeds, counter resets, normal schedule resumes",
            ok.Status == "ok" && rig2.Row.RetryAttempts == 0 && rig2.Row.NextRunAt == rig2.Clock.GetUtcNow() + 6 * H);

        // Auth problems are not retried (retrying would not help) and do not block the healthy source.
        var bad = new FakeConnector("x", _ => SourceFetchResult.Fail(FetchErrorKind.Auth, "token rejected"));
        var rig3 = Rig.Create(new[] { bad });
        rig3.Clock.Advance(6 * H);
        var r3 = (await rig3.Scheduler.TickAsync(default)).Single().Run!;
        T.Check("auth failure: no retry loop, normal schedule", r3.Status == "failed" && rig3.Row.RetryAttempts == 0 &&
            rig3.Row.NextRunAt == rig3.Clock.GetUtcNow() + 6 * H);
    }

    private static async Task NoReprocessing()
    {
        SourceSearchRequest? last = null;
        var rig0 = new Rig();
        var posts = rig0.Posts("0", "1", "2");
        var conn = new FakeConnector("x", req => { last = req; return new SourceFetchResult(posts, NextCursor: "cursor-2"); });
        var rig = Rig.Create(new[] { conn });
        posts = rig.Posts("0", "1", "2");

        await rig.Coordinator.RunAsync(rig.Profile.Id, RunTrigger.Manual, default);
        T.Eq("first run analysed 3 posts", 3, rig.Ai.Calls);
        T.Check("first run had no cursor", last?.SinceCursor is null);

        rig.Clock.Advance(H);
        await rig.Coordinator.RunAsync(rig.Profile.Id, RunTrigger.Manual, default);
        T.Eq("cursor is sent so the source can skip old posts", "cursor-2", last?.SinceCursor);
        T.Eq("even if the source returns them again, they are not analysed again", 3, rig.Ai.Calls);
        T.Eq("still 3 leads", 3, rig.Pipe.Leads.Count);
    }

    private static async Task RateLimits()
    {
        var rig0 = new Rig();
        int calls = 0;
        DateTimeOffset? reset = null;
        var limited = new FakeConnector("x", _ => { calls++; return SourceFetchResult.Fail(FetchErrorKind.RateLimited, "429", reset); });
        var rig = Rig.Create(new[] { limited });
        reset = rig.Clock.GetUtcNow() + TimeSpan.FromMinutes(10);

        var r1 = (await rig.Coordinator.RunAsync(rig.Profile.Id, RunTrigger.Manual, default)).Sources.Single();
        T.Check("429 is recorded as a failure with a reason", r1.Status == SourceRunStatus.Failed && r1.Error == "429");
        T.Eq("source is parked until the reset time", reset, rig.Pipe.Sources[0].RateLimitedUntil);

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        var r2 = (await rig.Coordinator.RunAsync(rig.Profile.Id, RunTrigger.Manual, default)).Sources.Single();
        T.Check("before the reset: skipped, API not called", r2.Status == SourceRunStatus.Skipped && calls == 1);

        rig.Clock.Advance(TimeSpan.FromMinutes(6));
        reset = rig.Clock.GetUtcNow() + TimeSpan.FromMinutes(10);
        await rig.Coordinator.RunAsync(rig.Profile.Id, RunTrigger.Manual, default);
        T.Eq("after the reset: called again", 2, calls);

        // Minimum gap between calls to the same source.
        int gapCalls = 0;
        var fast = new FakeConnector("x", _ => { gapCalls++; return new SourceFetchResult(Array.Empty<SourcePost>()); });
        var gapRig = Rig.Create(new[] { fast }, runOptions: new RunOptions(TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(15)));
        await gapRig.Coordinator.RunAsync(gapRig.Profile.Id, RunTrigger.Manual, default);
        gapRig.Clock.Advance(TimeSpan.FromSeconds(5));
        var gap = (await gapRig.Coordinator.RunAsync(gapRig.Profile.Id, RunTrigger.Manual, default));
        T.Check("5s later: skipped by the minimum gap", gapCalls == 1 && gap.Sources.Single().Status == SourceRunStatus.Skipped);
        T.Eq("skipped sources do not count as failures", "ok", gap.Status);
        gapRig.Clock.Advance(TimeSpan.FromSeconds(30));
        await gapRig.Coordinator.RunAsync(gapRig.Profile.Id, RunTrigger.Manual, default);
        T.Eq("after the gap: called again", 2, gapCalls);
        _ = rig0;
    }

    private static async Task Isolation()
    {
        // One source throws, the other is healthy.
        var rig0 = new Rig();
        var healthyPosts = rig0.Posts("0", "1");
        var healthy = new FakeConnector("reddit", _ => new SourceFetchResult(healthyPosts));
        var rig = Rig.Create(new ILeadSourceConnector[] { new ThrowingConnector("x"), healthy });
        healthyPosts.Clear(); healthyPosts.AddRange(rig.Posts("0", "1"));

        var run = await rig.Coordinator.RunAsync(rig.Profile.Id, RunTrigger.Manual, default);
        var x = run.Sources.Single(s => s.SourceType == "x");
        var reddit = run.Sources.Single(s => s.SourceType == "reddit");
        T.Check("failing source is reported with its error", x.Status == SourceRunStatus.Failed && x.Error == "connector exploded");
        T.Check("healthy source still collected its leads", reddit.Status == SourceRunStatus.Done && reddit.Leads == 2);
        T.Eq("leads saved despite the failure", 2, rig.Pipe.Leads.Count);
        T.Eq("overall status is partial", "partial", run.Status);
        T.Check("failed job is recorded", rig.Pipe.Jobs.Values.Any(j => j.Status == "failed" && j.Error == "connector exploded"));

        // Two due profiles; the store breaks for one of them.
        var ok = new FakeConnector("x", _ => new SourceFetchResult(Array.Empty<SourcePost>()));
        var rig2 = Rig.Create(new[] { ok });
        var other = Fx.Profile();
        rig2.Pipe.Profiles[other.Id] = other;
        rig2.AddSchedule(other.Id, ScheduleSettings.EverySixHours());
        rig2.Clock.Advance(7 * H);
        rig2.Sched.ThrowOnClaim.Add(rig2.Profile.Id);
        var items = await rig2.Scheduler.TickAsync(default);
        T.Check("one profile errors out", items.Any(i => i.ProfileId == rig2.Profile.Id && i.Error is not null));
        T.Check("the other profile still runs", items.Any(i => i.ProfileId == other.Id && i.Run?.Status == "ok"));

        // Unexpected crash inside a run is contained and scheduled for retry.
        var rig3 = Rig.Create(new[] { ok });
        rig3.Pipe.ThrowOnLoadFor = rig3.Profile.Id;
        rig3.Clock.Advance(7 * H);
        var crash = (await rig3.Scheduler.TickAsync(default)).Single();
        T.Check("crash is contained, run marked retrying, claim released",
            crash.Run?.Status == "retrying" && rig3.Row.RunningSince is null);
    }
}

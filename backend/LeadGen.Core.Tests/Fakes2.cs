using LeadGen.Core.Ai;
using LeadGen.Core.Connectors;
using LeadGen.Core.Pipeline;
using LeadGen.Core.Scheduling;

namespace LeadGen.Core.Tests;

/// <summary>In-memory schedule store with the same atomic-claim semantics as the database version.</summary>
public class InMemoryScheduleStore : IScheduleStore
{
    public class Row
    {
        public ScheduleSettings Settings = ScheduleSettings.Off;
        public DateTimeOffset? NextRunAt, LastRunAt, RunningSince;
        public string? LastStatus, LastError, Token;
        public int RetryAttempts;
        public int Claims;
    }

    private readonly object _gate = new();
    public readonly Dictionary<Guid, Row> Rows = new();
    public readonly HashSet<Guid> ThrowOnClaim = new();

    private ScheduledProfile Snap(Guid id, Row r) => new(id, r.Settings, r.NextRunAt, r.LastRunAt, r.LastStatus, r.RetryAttempts);

    public Task<ScheduledProfile?> GetAsync(Guid profileId, CancellationToken ct)
    { lock (_gate) return Task.FromResult(Rows.TryGetValue(profileId, out var r) ? Snap(profileId, r) : null); }

    public Task<IReadOnlyList<ScheduledProfile>> GetDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<ScheduledProfile>>(Rows
                .Where(kv => kv.Value.Settings.Mode != ScheduleMode.Off && !kv.Value.Settings.Paused && kv.Value.NextRunAt <= now)
                .Select(kv => Snap(kv.Key, kv.Value)).ToList());
    }

    public Task<string?> TryClaimAsync(Guid profileId, DateTimeOffset now, TimeSpan staleAfter, CancellationToken ct)
    {
        if (ThrowOnClaim.Contains(profileId)) throw new InvalidOperationException("claim failed");
        lock (_gate)
        {
            if (!Rows.TryGetValue(profileId, out var r)) return Task.FromResult<string?>(null);
            if (r.RunningSince is { } since && since > now - staleAfter) return Task.FromResult<string?>(null);
            r.RunningSince = now; r.Token = Guid.NewGuid().ToString("N"); r.Claims++;
            return Task.FromResult<string?>(r.Token);
        }
    }

    public Task CompleteRunAsync(Guid profileId, string claimToken, RunCompletion c, CancellationToken ct)
    {
        lock (_gate)
        {
            var r = Rows[profileId];
            if (r.Token != claimToken) return Task.CompletedTask;   // a stale run must not overwrite the current one
            r.RunningSince = null; r.Token = null;
            r.LastRunAt = c.FinishedAt; r.LastStatus = c.Status; r.LastError = c.Error;
            if (c.UpdateSchedule) { r.NextRunAt = c.NextRunAt; r.RetryAttempts = c.RetryAttempts; }
        }
        return Task.CompletedTask;
    }
}

public class CountingAi(IAiClient inner) : IAiClient
{
    public int Calls;
    public Task<(AiResult Result, string RawJson)> AnalyzeAsync(string content, IReadOnlyList<ExclusionRuleInput> exclusions, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return inner.AnalyzeAsync(content, exclusions, ct);
    }
}

/// <summary>Blocks inside FetchAsync until released, so tests can hold a run "in progress".</summary>
public class GatedConnector(string name, Func<SourceFetchResult> result) : ILeadSourceConnector
{
    public string SourceName => name;
    public int Calls;
    public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<SourceFetchResult> FetchAsync(SourceSearchRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        Entered.TrySetResult();
        await Gate.Task;
        return result();
    }
}

/// <summary>Everything a scheduling test needs, wired together with a fake clock and the real AI service.</summary>
public class Rig
{
    public InMemoryStore Pipe = new();
    public InMemoryScheduleStore Sched = new();
    public FakeClock Clock = new(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero));
    public CountingAi Ai = new(Fx.RealAi());
    public ProfileSnapshot Profile = Fx.Profile();
    public ProfileRunCoordinator Coordinator = null!;
    public SchedulerService Scheduler = null!;

    public Guid SourceId(string name) => Pipe.Sources.First(s => s.SourceType == name).Id;

    public static Rig Create(IEnumerable<ILeadSourceConnector> connectors, ScheduleSettings? settings = null, RunOptions? runOptions = null)
    {
        var rig = new Rig();
        foreach (var c in connectors) rig.Pipe.Sources.Add(new SourceState(Guid.NewGuid(), c.SourceName, true, null, null));
        rig.Pipe.Profile = rig.Profile;
        var runner = new PipelineRunner(rig.Pipe, rig.Ai, connectors, rig.Clock, runOptions ?? new RunOptions(TimeSpan.Zero, TimeSpan.FromMinutes(15)));
        rig.Coordinator = new ProfileRunCoordinator(rig.Sched, rig.Pipe, runner, rig.Clock);
        rig.Scheduler = new SchedulerService(rig.Sched, rig.Coordinator, rig.Clock);
        rig.AddSchedule(rig.Profile.Id, settings ?? ScheduleSettings.EverySixHours());
        return rig;
    }

    public void AddSchedule(Guid profileId, ScheduleSettings s) =>
        Sched.Rows[profileId] = new InMemoryScheduleStore.Row { Settings = s, NextRunAt = ScheduleCalculator.NextRun(s, Clock.GetUtcNow()) };

    public InMemoryScheduleStore.Row Row => Sched.Rows[Profile.Id];

    public List<SourcePost> Posts(params string[] ids) => ids.Select(i => Fx.Post(i, Texts[int.Parse(i) % Texts.Length], Clock.GetUtcNow())).ToList();

    private static readonly string[] Texts =
    {
        "Looking for a company to build our ERP system in Riyadh",
        "We need a software company to build our website in Cairo",
        "Can anyone recommend a team to build an iOS application? Based in Dubai",
        "Looking for a developer to build a mobile app for our clinic in Jeddah",
        "Need a company to develop an online store for our shop in Alexandria",
    };
}

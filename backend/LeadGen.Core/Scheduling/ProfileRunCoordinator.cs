using LeadGen.Core.Pipeline;

namespace LeadGen.Core.Scheduling;

/// <summary>
/// The only way a profile gets run, for both the Run button and the scheduler. It takes an atomic claim first,
/// so the same profile never runs twice at once, then records last/next run and applies the retry policy.
/// </summary>
public class ProfileRunCoordinator(IScheduleStore schedules, IPipelineStore pipeline, PipelineRunner runner,
    TimeProvider? clock = null, SchedulerOptions? options = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SchedulerOptions _options = options ?? SchedulerOptions.Default;

    public async Task<CoordinatedRun> RunAsync(Guid profileId, RunTrigger trigger, CancellationToken ct)
    {
        var token = await schedules.TryClaimAsync(profileId, _clock.GetUtcNow(), _options.StaleClaimAfter, ct);
        if (token is null) return new(CoordinatedOutcome.AlreadyRunning, Array.Empty<SourceRunResult>(), "already_running", null);

        IReadOnlyList<SourceRunResult> results = Array.Empty<SourceRunResult>();
        string? crash = null;
        try
        {
            var profile = await pipeline.LoadProfileAsync(profileId, ct);
            if (profile is null)
            {
                await schedules.CompleteRunAsync(profileId, token,
                    new(_clock.GetUtcNow(), "not_found", "Search profile not found", false, null, 0), CancellationToken.None);
                return new(CoordinatedOutcome.ProfileNotFound, results, "not_found", null);
            }
            results = await runner.RunProfileAsync(profile, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: release the claim without touching the schedule so the run is picked up again.
            await schedules.CompleteRunAsync(profileId, token,
                new(_clock.GetUtcNow(), "cancelled", null, false, null, 0), CancellationToken.None);
            throw;
        }
        catch (Exception ex) { crash = ex.Message; }

        var finished = _clock.GetUtcNow();
        var failed = results.Count(r => r.Status == SourceRunStatus.Failed);
        var done = results.Count(r => r.Status == SourceRunStatus.Done);
        var retriable = crash is not null || results.Any(r => r.Status == SourceRunStatus.Failed && r.Retriable);

        var error = crash ?? results.FirstOrDefault(r => r.Status == SourceRunStatus.Failed)?.Error;
        var status = crash is not null || (failed > 0 && done == 0) ? "failed" : failed > 0 ? "partial" : "ok";

        // Read the schedule again: the owner may have paused or edited it while this run was in progress.
        var current = await schedules.GetAsync(profileId, CancellationToken.None);
        DateTimeOffset? next = null;
        var attempts = 0;
        var update = trigger == RunTrigger.Scheduled && current is not null;

        if (update)
        {
            if (retriable)
            {
                var n = current!.RetryAttempts + 1;
                if (RetryPolicy.DelayAfter(n) is { } delay && !current.Settings.Paused && current.Settings.Mode != ScheduleMode.Off)
                {
                    next = finished + delay;
                    attempts = n;
                    status = "retrying";
                }
                else next = ScheduleCalculator.NextRun(current.Settings, finished);   // retries used up: back to the normal schedule
            }
            else next = ScheduleCalculator.NextRun(current!.Settings, finished);
        }

        await schedules.CompleteRunAsync(profileId, token, new(finished, status, error, update, next, attempts), CancellationToken.None);
        return new(CoordinatedOutcome.Completed, results, status, next);
    }
}

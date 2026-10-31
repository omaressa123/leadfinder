namespace LeadGen.Core.Scheduling;

public record TickItem(Guid ProfileId, CoordinatedRun? Run, string? Error);

/// <summary>One scheduler tick: run every due profile. A problem with one profile never stops the others.</summary>
public class SchedulerService(IScheduleStore schedules, ProfileRunCoordinator coordinator, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<IReadOnlyList<TickItem>> TickAsync(CancellationToken ct)
    {
        var items = new List<TickItem>();
        foreach (var due in await schedules.GetDueAsync(_clock.GetUtcNow(), ct))
        {
            ct.ThrowIfCancellationRequested();
            try { items.Add(new TickItem(due.ProfileId, await coordinator.RunAsync(due.ProfileId, RunTrigger.Scheduled, ct), null)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { items.Add(new TickItem(due.ProfileId, null, ex.Message)); }
        }
        return items;
    }
}

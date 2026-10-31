using LeadGen.Core.Scheduling;

namespace LeadGen.Api;

/// <summary>Wakes up every few seconds and runs whatever is due. Errors are logged, never fatal.</summary>
public class SchedulerWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<SchedulerWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!config.GetValue("Scheduler:Enabled", true)) { log.LogInformation("Scheduler disabled"); return; }
        var every = TimeSpan.FromSeconds(Math.Max(5, config.GetValue("Scheduler:TickSeconds", 30)));
        using var timer = new PeriodicTimer(every);
        try
        {
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var items = await scope.ServiceProvider.GetRequiredService<SchedulerService>().TickAsync(stop);
                    foreach (var i in items)
                        log.LogInformation("Scheduled run {Profile}: {Status} {Error}", i.ProfileId, i.Run?.Status, i.Error);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception ex) { log.LogError(ex, "Scheduler tick failed"); }
            } while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { }
    }
}

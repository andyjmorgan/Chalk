namespace Chalk;

/// <summary>Deletes expired revisions and tombstoned keys on a timer. Single leader via a Postgres advisory lock.</summary>
public sealed class Sweeper(Store store, IConfiguration cfg, ILogger<Sweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var every = TimeSpan.FromSeconds(cfg.GetValue("Sweep:IntervalSeconds", 300));
        await Task.Delay(TimeSpan.FromSeconds(20), ct).ContinueWith(_ => { }, CancellationToken.None);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (expired, trimmed, tomb, events) = await store.SweepAsync(ct);
                if (expired + trimmed + tomb + events > 0)
                    log.LogInformation("Sweep: {Expired} keys expired, {Trimmed} old revisions, {Tomb} tombstones, {Events} events removed", expired, trimmed, tomb, events);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception e) { log.LogError(e, "Sweep failed"); }
            try { await Task.Delay(every, ct); } catch (OperationCanceledException) { }
        }
    }
}

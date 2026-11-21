using System.Threading.Channels;

namespace CSweet.Hosting;

internal sealed class DeploymentWorker(DeploymentStore store, IServiceProvider services, IWorkspaceIngress ingress,
    IReadinessProbe probe, ILogger<DeploymentWorker> log) : BackgroundService
{
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(1);
    public void Wake() => wake.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Parallel.ForEachAsync(store.Outstanding(), new ParallelOptions
            {
                CancellationToken = stoppingToken, MaxDegreeOfParallelism = 4
            }, async (id, ct) =>
            {
                try { await ReconcileAsync(id, ct); }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // Responses and exception text can contain provider credentials.
                    log.LogWarning("Deployment {Id} will retry ({Error})", id, ex.GetType().Name);
                }
            });
            using var interval = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            interval.CancelAfter(TimeSpan.FromSeconds(10));
            try { await wake.Reader.ReadAsync(interval.Token); }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
        }
    }

    internal async Task ReconcileAsync(Guid id, CancellationToken ct)
    {
        var row = store.Find(id)!;
        var provider = services.GetRequiredKeyedService<IComputeProvider>(row.Pool);
        var allocation = new Allocation(row.Name, row.Template);
        switch (row.Phase)
        {
            case "retiring":
                if (await provider.RemoveAsync(allocation, ct))
                {
                    await ingress.RemoveAsync(row.Name, row.Host, ct);
                    store.Transition(id, "retiring", "retired");
                }
                return;
            case "pending":
                var token = await ingress.EnsureAsync(row.Name, row.Host, ct);
                await provider.PrepareAsync(allocation, new(id.ToString(), row.Purchase.OwnerId, row.Host,
                    row.Release, row.ProofKey, token), ct);
                if (!store.Transition(id, "pending", "allocating")) return;
                goto case "allocating";
            case "allocating":
                await provider.EnsureRunningAsync(allocation, ct);
                if (!store.Transition(id, "allocating", "verifying")) return;
                goto case "verifying";
            case "verifying":
                if (await probe.VerifyAsync(row, ct)) store.Transition(id, "verifying", "ready");
                return;
        }
    }
}

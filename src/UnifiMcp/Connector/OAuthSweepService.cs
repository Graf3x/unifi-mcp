using Microsoft.Extensions.Hosting;

namespace UnifiMcp;

/// <summary>Removes expired OAuth rows at startup and then every hour. Logs only counts, never row contents.</summary>
/// <remarks>
/// The ledger (and so the store) is resolved inside the try, and only while the public side is ready: opening it can fail (a corrupt
/// oauth.db), the startup check has already said so once, and a hosted service that throws would stop the host.
/// </remarks>
public sealed class OAuthSweepService(IServiceProvider services, ConnectorState state, ILogger<OAuthSweepService> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            if (!state.Ready) continue;
            try
            {
                var removed = await services.GetRequiredService<GrantLedger>().SweepAsync(stoppingToken);
                if (removed > 0) log.LogInformation("OAuth sweep removed {Count} expired rows", removed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "OAuth sweep failed; will retry next interval");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}

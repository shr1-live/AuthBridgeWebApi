using AuthBridge.Application.Common;
using AuthBridge.Application.Services;
using Microsoft.Extensions.Options;

namespace AuthBridge.Api.Worker;

/// <summary>Lets a submission wake the worker instead of waiting for the next idle poll.</summary>
public sealed class SimulationSignal : ISimulationSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        if (_signal.CurrentCount == 0)
        {
            try { _signal.Release(); } catch (SemaphoreFullException) { }
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>
/// Runs the durable simulator inside the API process. It works only while the service is
/// awake: on a Render free instance that spins down, pending decisions wait until the next
/// request wakes it. All progress is in the database, so a restart resumes where it stopped.
/// </summary>
public sealed class SimulationWorker(
    IServiceScopeFactory scopes,
    SimulationSignal signal,
    IOptions<SimulationOptions> options,
    ILogger<SimulationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var idle = settings.IdlePollInitial;
        logger.LogInformation("Payer simulator started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = false;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var simulator = scope.ServiceProvider.GetRequiredService<IPayerSimulationService>();
                processed = await simulator.ProcessNextAsync(SimulationContext.Create(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Bounded: a failing database backs the loop off rather than spinning.
                logger.LogWarning(ex, "Simulator step failed; backing off");
            }

            if (processed)
            {
                idle = settings.IdlePollInitial;
                continue;
            }

            try
            {
                await signal.WaitAsync(idle, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            idle = TimeSpan.FromTicks(Math.Min(idle.Ticks * 2, settings.IdlePollMax.Ticks));
        }
        logger.LogInformation("Payer simulator stopped");
    }
}

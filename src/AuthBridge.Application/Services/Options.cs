namespace AuthBridge.Application.Services;

public sealed class WorkflowOptions
{
    public const string Section = "Workflow";

    /// <summary>Fixed by the specification: approval expires five minutes after proposal creation.</summary>
    public static readonly TimeSpan ProposalLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Angular origin used to build proposal review links.</summary>
    public string ReviewUrlBase { get; set; } = "http://localhost:4200";
}

public sealed class SimulationOptions
{
    public const string Section = "Simulation";

    public bool Enabled { get; set; } = true;

    /// <summary>Minimum dwell between simulated payer steps, so progress is observable.</summary>
    public TimeSpan StepDelay { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    public int MaxFailures { get; set; } = 3;

    public TimeSpan IdlePollInitial { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan IdlePollMax { get; set; } = TimeSpan.FromSeconds(60);
}

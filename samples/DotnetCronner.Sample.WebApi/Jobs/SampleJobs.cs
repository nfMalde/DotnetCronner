using DotnetCronner;

namespace DotnetCronner.Sample.WebApi.Jobs;

/// <summary>A service the jobs depend on, to show DI-based parameter resolution.</summary>
public interface IGreeter
{
    void Greet(string who);
}

/// <inheritdoc />
public sealed class ConsoleGreeter(ILogger<ConsoleGreeter> logger) : IGreeter
{
    /// <inheritdoc />
    public void Greet(string who) => logger.LogInformation("Hello {Who} from DotnetCronner at {Time:O}", who, DateTimeOffset.UtcNow);
}

/// <summary>Demonstrates both attribute-based and lambda-based task registration.</summary>
public sealed class SampleJobs(IGreeter greeter)
{
    /// <summary>Attribute task: runs every minute, all parameters resolved from DI.</summary>
    [CronnerTask(id: "heartbeat", cronstring: "* * * * *", Priority = CronnerTaskPriority.High)]
    public Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        greeter.Greet("heartbeat");
        return Task.CompletedTask;
    }

    /// <summary>Scheduled via a lambda in Program.cs using HasParam markers.</summary>
    public void SayHello(string who, CancellationToken cancellationToken) => greeter.Greet(who);
}

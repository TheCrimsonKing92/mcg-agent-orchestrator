namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class WindowsWorkerSandboxReader : IWorkerSandboxReader
{
    public WorkerSandboxReading Read()
    {
        if (!OperatingSystem.IsWindows()) return WorkerSandboxReading.Unavailable("unsupported-platform");
        try
        {
            return WorkerSandboxReading.Available(WorkerSandboxOptions.FromEnvironment().Enabled,
                Environment.GetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable));
        }
        catch (Exception exception)
        {
            return WorkerSandboxReading.Unavailable($"worker-sandbox-error:{exception.GetType().Name}");
        }
    }
}

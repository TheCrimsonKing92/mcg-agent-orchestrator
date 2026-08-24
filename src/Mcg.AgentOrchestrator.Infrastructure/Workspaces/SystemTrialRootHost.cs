using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class SystemTrialRootHost : ITrialRootHost
{
    private readonly DisposableTrialRoot _roots = new();

    public ITrialRootSession Create(TrialRootRequest request) =>
        new SystemTrialRootSession(_roots.Create(request));

    private sealed class SystemTrialRootSession(TrialRootLease lease) : ITrialRootSession
    {
        public string RootPath => lease.RootPath;

        public string ResolvedBaseCommit => lease.ResolvedBaseCommit;

        public string HarnessStatePath => lease.HarnessStatePath;

        public ITrialLaunch Start(ProcessStartInfo command) => new SystemTrialLaunch(lease.Start(command));

        public TrialTeardownReport Destroy() => lease.Destroy();

        public void Dispose() => lease.Dispose();
    }

    private sealed class SystemTrialLaunch(TrialProcessHandle handle) : ITrialLaunch
    {
        public string StdoutPath => handle.StdoutPath;

        public string StderrPath => handle.StderrPath;

        public int ExitCode => handle.ExitCode;

        public bool WaitForExit(int milliseconds) => handle.WaitForExit(milliseconds);

        public void Dispose() => handle.Dispose();
    }
}

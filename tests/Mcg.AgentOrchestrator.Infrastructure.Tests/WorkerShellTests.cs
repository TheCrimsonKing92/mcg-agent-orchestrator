using System.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerShellTests
{
    [Xunit.Fact(DisplayName = "WorkerShell_resolves_a_powershell_host")]
    public void WorkerShellResolvesAPowerShellHost()
    {
        // Resolves cross-platform pwsh when present, else Windows PowerShell on Windows.
        Assert.True(WorkerShell.Executable is "pwsh" or "powershell.exe");
    }

    [Xunit.Fact(DisplayName = "WorkerShell_base_arguments_end_with_command")]
    public void WorkerShellBaseArgumentsEndWithCommand()
    {
        var args = WorkerShell.BaseArguments();
        Assert.True(args.Any(a => a == "-NoProfile"));
        // -Command must be last so the wrapper/script follows it.
        Assert.Equal("-Command", args[^1]);
        // -ExecutionPolicy is Windows-only.
        Assert.Equal(OperatingSystem.IsWindows(), args.Any(a => a == "-ExecutionPolicy"));
    }
}

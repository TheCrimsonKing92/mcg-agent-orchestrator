using System.Runtime.InteropServices;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class GoalRefinementDetachedLaunchArgumentTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Xunit.Fact]
    public void RefinementRequestSurvivesChildStartupNormalization()
    {
        var (request, goalId, stamp) = CreateRequest();

        AssertChildArgs(request.Args, goalId, stamp);
    }

    [Xunit.Fact(Skip = "Requires Windows command-line parsing.", SkipUnless = nameof(IsWindows))]
    public void RefinementRequestSurvivesWindowsDetachedCommandLine()
    {
        var (request, goalId, stamp) = CreateRequest();
        var command = new[] { @"C:\Program Files\mcg\orchestrator.exe" }
            .Concat(request.Args)
            .ToArray();
        var rendered = ConductorLoopHandoff.BuildWindowsProcessCommandLine(command);
        var childArgv = ParseWindowsCommandLine(rendered).Skip(1).ToArray();

        AssertChildArgs(childArgv, goalId, stamp);
    }

    private static (ConductLoopLaunchRequest Request, string GoalId, string Stamp) CreateRequest()
    {
        const string goalId = "1234567890abcdef1234567890abcdef";
        const string stamp = "20260924125201945";
        var workspace = OrchestratorWorkspace.ForProject(
            "refinement", Path.Combine(Path.GetTempPath(), "goal-refinement-argv-test"));
        var request = GoalRefinementWorkCoordinator.CreateLaunchRequest(
            workspace, new GoalId(goalId), stamp);
        return (request, goalId, stamp);
    }

    private static void AssertChildArgs(IReadOnlyList<string> childArgv, string goalId, string stamp)
    {
        var project = OrchestratorProjectSelection.FromArgs(childArgv, null);
        var tenant = OrchestratorTenantSelection.FromArgs(project.CommandArgs, null);
        var normalized = CliArgumentParser.NormalizeArgs(tenant.CommandArgs.ToArray());

        Xunit.Assert.Equal("refinement", project.ProjectName);
        Xunit.Assert.Equal(3, normalized.Count);
        Xunit.Assert.Equal(GoalRefinementWorkCoordinator.CommandName, normalized[0]);
        Xunit.Assert.Equal(goalId, normalized[1]);
        Xunit.Assert.Equal(stamp, normalized[2]);
    }

    private static string[] ParseWindowsCommandLine(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            return Enumerable.Range(0, count)
                .Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size))!)
                .ToArray();
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}

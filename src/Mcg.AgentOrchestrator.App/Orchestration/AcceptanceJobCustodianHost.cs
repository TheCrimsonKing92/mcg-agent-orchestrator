using System.Runtime.InteropServices;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptanceJobCustodianHost
{
    internal const string Subcommand = "__acceptance-custodian";

    internal static AcceptanceJobCustodianLaunchSpecification Bind(OrchestratorWorkspace workspace) =>
        Bind(Environment.ProcessPath, typeof(AcceptanceJobCustodianHost).Assembly.Location,
            RuntimeEnvironment.GetRuntimeDirectory(), workspace);

    internal static AcceptanceJobCustodianLaunchSpecification Bind(string? processPath, string application,
        string runtimeDirectory, OrchestratorWorkspace workspace)
    {
        // Derive the muxer from the loaded shared runtime, never PATH or an environment claim.
        // A dotnet-hosted caller must agree with that runtime's installation as well.
        if (!Path.IsPathFullyQualified(runtimeDirectory) || !Path.IsPathFullyQualified(application))
            throw new InvalidOperationException("Acceptance custody cannot establish the current managed runtime closure.");
        var root = Directory.GetParent(Path.TrimEndingDirectorySeparator(runtimeDirectory))?.Parent?.Parent?.FullName;
        var host = root is null ? null : Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (host is null || !Path.IsPathFullyQualified(host) || !File.Exists(host) ||
            string.IsNullOrWhiteSpace(processPath) || !Path.IsPathFullyQualified(processPath) ||
            (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                !WorkspaceInvocationBinding.SamePath(host, processPath)) ||
            !WorkspaceInvocationBinding.SamePath(application, typeof(AcceptanceJobCustodianHost).Assembly.Location) ||
            !File.Exists(Path.ChangeExtension(application, ".runtimeconfig.json")) ||
            !File.Exists(Path.ChangeExtension(application, ".deps.json")))
            throw new InvalidOperationException("Acceptance custody cannot establish the current managed runtime closure.");
        var args = new List<string>
        {
            application, "--repository-root", workspace.RootDirectory,
            "--expected-state-directory", workspace.OrchestratorDirectory,
            "--project", workspace.ProjectName, "--tenant", workspace.TenantName, Subcommand
        };
        return new(host, args, workspace.ExecutionDirectory);
    }

    internal static int Run(IReadOnlyList<string> args)
    {
        if (args.Count != 1 || args[0] != Subcommand)
        {
            Console.Error.WriteLine("Invalid acceptance custody command.");
            return 1;
        }
        return AcceptanceJobCustodianNative.Run(Console.In, Console.Out, Console.Error);
    }
}

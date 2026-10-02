using System.ComponentModel;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliConductorCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("conductor", StringComparison.OrdinalIgnoreCase);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        IConductorProcessLauncher? launcher = null, IConductorLockProbe? lockProbe = null,
        Func<DateTimeOffset>? bootTime = null, Func<string?>? mainCommit = null,
        TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        lockProbe ??= new SystemConductorLockProbe();
        if (args.Count < 2 || args.Count > 3 ||
            args.Count == 3 && !(args[1].Equals("start", StringComparison.OrdinalIgnoreCase) &&
                args[2].Equals("--clear-stop", StringComparison.OrdinalIgnoreCase)))
        {
            error.WriteLine(CliCommandHelp.ConductorUsage);
            return 1;
        }
        try
        {
            var owner = lockProbe.ActiveOwnerPid(workspace);
            switch (args[1].ToLowerInvariant())
            {
                case "start":
                    return Start(workspace, owner, args.Count == 3, launcher ?? new SystemConductorProcessLauncher(), output, error);
                case "status":
                    CliConductorStatusReader.Print(workspace, owner,
                        (bootTime ?? (() => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64)))(), output,
                        mainCommit ?? (() => ResolveMainCommit(workspace)));
                    return 0;
                case "stop":
                    if (owner is null)
                    {
                        output.WriteLine("No conductor is running; nothing to stop.");
                        return 0;
                    }
                    var stopFile = Path.Combine(workspace.ExecutionDirectory, ConductorBatchLoop.StopFileName);
                    using (new FileStream(stopFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) { }
                    output.WriteLine($"Stop requested for conductor pid {owner}. {stopFile}");
                    output.WriteLine("This is a detach, not a drain: live workers are detached without waiting for them to finish.");
                    return 0;
                default:
                    error.WriteLine(CliCommandHelp.ConductorUsage);
                    return 1;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            Win32Exception or TimeoutException or JsonException)
        {
            error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static string? ResolveMainCommit(OrchestratorWorkspace workspace)
    {
        try
        {
            var result = GitCli.Run(workspace.ExecutionDirectory, "rev-parse", "--verify", "--quiet", "refs/heads/main");
            if (!result.Succeeded || result.DrainTimedOut) return null;
            var commit = result.Output.Trim();
            return commit.Length == 0 ? null : commit;
        }
        catch (Exception)
        {
            // Main lookup is optional status evidence; git failures must keep status successful.
            return null;
        }
    }

    private static int Start(OrchestratorWorkspace workspace, int? owner, bool clearStop,
        IConductorProcessLauncher launcher, TextWriter output, TextWriter error)
    {
        if (owner is not null)
        {
            error.WriteLine($"Refused: conductor already running (pid {owner}).");
            return 1;
        }
        var stopFile = Path.Combine(workspace.ExecutionDirectory, ConductorBatchLoop.StopFileName);
        if (File.Exists(stopFile))
        {
            if (!clearStop)
            {
                error.WriteLine($"Refused: stop file exists: {stopFile}. Use --clear-stop to remove it and start.");
                return 1;
            }
            File.Delete(stopFile);
        }
        var request = new ConductorLaunchRequest("pwsh",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(workspace.ExecutionDirectory, "scripts", "Start-OrchestratorCommand.ps1"),
                "-Name", "conduct-loop-daemon", "conduct", "--loop", "--daemon", "--watch",
                "--poll-seconds", "120", "--max-duration", "43200"],
            workspace.ExecutionDirectory,
            new Dictionary<string, string> { ["MCG_DISPATCH_MAX_RUNTIME_MIN"] = "120" },
            [CliProtectedProcessEnvironment.ProtectedPidVariable,
                CliProtectedProcessEnvironment.ProtectedStartTicksVariable]);
        var result = launcher.Launch(request);
        if (result.ExitCode != 0)
        {
            error.WriteLine(result.ExitCode == 2
                ? $"Refused: stop file exists: {stopFile}."
                : $"Conductor launcher failed (exit {result.ExitCode}): {result.StandardError}");
            return 1;
        }
        using var receipt = JsonDocument.Parse(result.StandardOutput ?? "");
        var pid = receipt.RootElement.GetProperty("pid").GetInt32();
        var stdoutPath = receipt.RootElement.GetProperty("stdoutPath").GetString();
        output.WriteLine($"Conductor launched: pid {pid}; stdout log: {stdoutPath}");
        output.WriteLine("Check status: mcg-orchestrator.cmd conductor status");
        return 0;
    }
}

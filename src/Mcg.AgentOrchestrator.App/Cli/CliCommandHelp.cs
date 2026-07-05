using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliCommandHelp
{
    public const string ConductUsage = "Usage: conduct <goal-id-prefix> [--policy <Conservative|Permissive|Manual>] [--watch [--poll-seconds <n>]], or conduct --loop [--max-iterations <n>] [--max-duration <seconds>] [--watch|--daemon] [--poll-seconds <n>]";
    public const string GoalUsage = "Usage: goal <objective> [--simple] [--from-backlog] [--run --confirm-batch-start]";
    public const string WorkspaceUsage = "Usage: workspace [create|merge|rebase|remove] [goal-id-prefix]";
    public const string WorkspaceCreateUsage = "Usage: workspace create [goal-id-prefix]";
    public const string ReassignAgentUsage = "Usage: reassign-agent <task-number> <agent-id>|<goal-prefix> <task-number> <agent-id>|--goal <goal-prefix> <task-number> <agent-id>";
    public const string BacklogListUsage = "Usage: backlog-list [--all] [--limit <n>] [--status <value>] [--text <pattern>]";
    public const string BacklogTriageUsage = "Usage: backlog-triage [--limit <n>] [--stale-days <n>]";
    public const string BacklogAddUsage = "Usage: backlog-add <title> [body] | backlog-add <title> --body-file <path>";
    public const string BacklogShowUsage = "Usage: backlog-show <id-prefix>";
    public const string BacklogCloseUsage = "Usage: backlog-close <id-prefix> [reason] | backlog-close <id-prefix> --reason-file <path>";
    public const string BacklogReopenUsage = "Usage: backlog-reopen <id-prefix> [reason]";
    public const string BacklogViewUsage = "Usage: backlog-view";
    public const string DogfoodLogUsage = "Usage: dogfood-log list [--limit <n>] | dogfood-log add [goal-prefix]";
    public const string OperatorCommandsUsage = "Usage: operator-commands [--help]";

    private static readonly CommandHelpEntry Conduct = new(
        ConductUsage,
        "Drive one goal or run the autonomous conductor loop.",
        [
            "--policy",
            "--loop",
            "--max-iterations",
            "--max-duration",
            "--watch",
            "--daemon",
            "--poll-seconds",
            "--watch-interval",
            "--quiet",
            "--stall-warning-seconds",
            "--stall-warning-minutes",
            "--help",
            "-h"
        ]);

    private static readonly CommandHelpEntry Goal = new(
        GoalUsage,
        "Create a goal.",
        new[] { "--simple", "--from-backlog", "--run", "--confirm-batch-start", "--brief-file", "--help", "-h" }
            .ToHashSet(StringComparer.OrdinalIgnoreCase),
        ValidateFlags: false);

    private static readonly CommandHelpEntry Workspace = new(
        WorkspaceUsage,
        "Manage goal worktrees for create, merge, rebase, and remove operations.",
        ["--autonomy", "--help", "-h"]);

    private static readonly CommandHelpEntry WorkspaceCreate = new(
        WorkspaceCreateUsage,
        "Create or reuse the worktree for a goal.",
        ["--autonomy", "--help", "-h"]);

    private static readonly CommandHelpEntry ReassignAgent = new(
        ReassignAgentUsage,
        "Persist an exact agent assignment for a task.",
        ["--goal", "--help", "-h"]);

    private static readonly CommandHelpEntry BacklogList = new(
        BacklogListUsage,
        "List backlog items from the backlog store.",
        ["--all", "--limit", "--status", "--text", "--help", "-h"]);

    private static readonly CommandHelpEntry BacklogTriage = new(
        BacklogTriageUsage,
        "Print compact backlog triage buckets for daemon curation.",
        ["--limit", "--stale-days", "--help", "-h"]);

    private static readonly CommandHelpEntry BacklogAdd = new(
        BacklogAddUsage,
        "Add a backlog item.",
        ["--body-file", "--help", "-h"]);

    private static readonly CommandHelpEntry BacklogShow = new(
        BacklogShowUsage,
        "Show a backlog item by id prefix.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry BacklogClose = new(
        BacklogCloseUsage,
        "Close a backlog item by id prefix.",
        ["--reason-file", "--help", "-h"]);

    private static readonly CommandHelpEntry BacklogReopen = new(
        BacklogReopenUsage,
        "Reopen a backlog item by id prefix.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry BacklogView = new(
        BacklogViewUsage,
        "Render all backlog items as markdown.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry DogfoodLog = new(
        DogfoodLogUsage,
        "Read or add dogfood goal-boundary entries in the SQLite dogfood log store.",
        ["--limit", "--help", "-h"]);

    private static readonly CommandHelpEntry OperatorCommands = new(
        OperatorCommandsUsage,
        "List approved repo-bounded command prefixes for the normal operator loop.",
        ["--help", "-h"],
        [
            "Observe: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Get-OrchestratorSnapshot.ps1 -GoalPrefix <goal>",
            "Wait: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Wait-RepoInterval.ps1 -Seconds <n>",
            "Wait dispatch: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Wait-ForDispatch.ps1 -ExitFile <path>",
            "Git/diff: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Invoke-Git.ps1 status --short",
            "Backlog/orchestrator: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Invoke-OrchestratorCommand.ps1 backlog-list --limit <n>",
            "Process inspect: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Get-RepoProcessInfo.ps1 -Id <pid>",
            "Exact stop: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Stop-RepoProcess.ps1 -Id <pid>",
            "SQLite: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Invoke-OrchestratorSqliteTool.ps1 <args>",
            "Logs: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Show-OrchestratorLogArtifacts.ps1 -GoalPrefix <goal> [-TaskPrefix <task>] [-TailLines <n>]",
            "Acceptance: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Invoke-OrchestratorCommand.ps1 acceptance <goal>"
        ]);

    private static readonly IReadOnlySet<string> GenericHelpFlags =
        new[] { "--help", "-h" }.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool TryPrintStartupHelp(IReadOnlyList<string> args)
    {
        if (TryPrintHelpCommand(args))
        {
            return true;
        }

        if (!TryResolveEntry(args, out var entry) || !HasHelpFlag(args))
        {
            return false;
        }

        Print(entry);
        return true;
    }

    internal static bool IsCommandSpecificHelp(IReadOnlyList<string> args)
    {
        return TryResolveHelpCommand(args, out _, out _) || (HasHelpFlag(args) && TryResolveEntry(args, out _));
    }

    internal static void ThrowIfInvalidFlags(IReadOnlyList<string> args)
    {
        if (!TryResolveEntry(args, out var entry))
        {
            return;
        }

        if (!entry.ValidateFlags)
        {
            return;
        }

        foreach (var arg in args.Skip(1).Where(IsFlag))
        {
            if (!entry.Flags.Contains(arg))
            {
                throw new ArgumentException($"Unknown option '{arg}'.{Environment.NewLine}{entry.Usage}");
            }
        }
    }

    private static bool TryResolveEntry(IReadOnlyList<string> args, out CommandHelpEntry entry)
    {
        entry = default;
        if (args.Count == 0)
        {
            return false;
        }

        if (args[0].Equals("conduct", StringComparison.OrdinalIgnoreCase))
        {
            entry = Conduct;
            return true;
        }

        if (args[0].Equals("goal", StringComparison.OrdinalIgnoreCase))
        {
            entry = Goal;
            return true;
        }

        if (args[0].Equals("reassign-agent", StringComparison.OrdinalIgnoreCase))
        {
            entry = ReassignAgent;
            return true;
        }

        if (args[0].Equals("backlog-list", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogList;
            return true;
        }

        if (args[0].Equals("backlog-triage", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogTriage;
            return true;
        }

        if (args[0].Equals("backlog-add", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogAdd;
            return true;
        }

        if (args[0].Equals("backlog-show", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogShow;
            return true;
        }

        if (args[0].Equals("backlog-close", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogClose;
            return true;
        }

        if (args[0].Equals("backlog-reopen", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogReopen;
            return true;
        }

        if (args[0].Equals("backlog-view", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogView;
            return true;
        }

        if (args[0].Equals("dogfood-log", StringComparison.OrdinalIgnoreCase))
        {
            entry = DogfoodLog;
            return true;
        }

        if (args[0].Equals("operator-commands", StringComparison.OrdinalIgnoreCase))
        {
            entry = OperatorCommands;
            return true;
        }

        if (!args[0].Equals("workspace", StringComparison.OrdinalIgnoreCase))
        {
            if (CliArgumentParser.IsRecognizedCommand(args[0]))
            {
                entry = CommandHelpEntry.Generic(args[0]);
                return true;
            }

            return false;
        }

        if (args.Count > 1 && args[1].Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            entry = WorkspaceCreate;
            return true;
        }

        entry = Workspace;
        return true;
    }

    private static bool TryPrintHelpCommand(IReadOnlyList<string> args)
    {
        if (!TryResolveHelpCommand(args, out var target, out var entry))
        {
            return false;
        }

        if (entry is { } resolved)
        {
            Print(resolved);
            return true;
        }

        throw new ArgumentException(CliArgumentParser.FormatUnknownCommandMessage([target]));
    }

    private static bool TryResolveHelpCommand(
        IReadOnlyList<string> args,
        out string target,
        out CommandHelpEntry? entry)
    {
        target = "";
        entry = null;
        if (args.Count < 2 || !args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        target = args[1];
        if (TryResolveEntry([target], out var resolved))
        {
            entry = resolved;
        }

        return true;
    }

    private static void Print(CommandHelpEntry entry)
    {
        Console.WriteLine(entry.Usage);
        Console.WriteLine();
        Console.WriteLine(entry.Description);
        Console.WriteLine();
        Console.WriteLine("Options:");
        var flags = entry.Flags
            .Where(flag => flag.StartsWith("-", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (flags.Contains("-h", StringComparer.OrdinalIgnoreCase) &&
            flags.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("  -h, --help");
            flags = flags
                .Where(flag => !flag.Equals("-h", StringComparison.OrdinalIgnoreCase) &&
                               !flag.Equals("--help", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        foreach (var flag in flags)
        {
            if (flag.Equals("--poll-seconds", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {flag} <n>    Positive integer seconds between watch polls; default {ConductorBatchLoop.DefaultWatchIntervalSeconds}.");
            }
            else if (flag.Equals("--watch-interval", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {flag} <n>    Legacy alias for --poll-seconds.");
            }
            else if (flag.Equals("--limit", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {flag} <n>");
            }
            else if (flag.Equals("--status", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {flag} <value>");
            }
            else if (flag.Equals("--text", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {flag} <pattern>");
            }
            else
            {
                Console.WriteLine($"  {flag}");
            }
        }

        if (entry.ExtraLines.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Approved prefixes:");
            foreach (var line in entry.ExtraLines)
            {
                Console.WriteLine($"  {line}");
            }
        }
    }

    private static bool HasHelpFlag(IReadOnlyList<string> args)
    {
        return args.Any(arg =>
            arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("-h", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFlag(string arg) =>
        arg.StartsWith("-", StringComparison.Ordinal) &&
        !int.TryParse(arg, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _);

    private readonly record struct CommandHelpEntry(
        string Usage,
        string Description,
        IReadOnlySet<string> Flags,
        IReadOnlyList<string> ExtraLines,
        bool ValidateFlags)
    {
        public CommandHelpEntry(string usage, string description, IEnumerable<string> flags)
            : this(usage, description, flags.ToHashSet(StringComparer.OrdinalIgnoreCase), [], ValidateFlags: true)
        {
        }

        public CommandHelpEntry(string usage, string description, IEnumerable<string> flags, IReadOnlyList<string> extraLines)
            : this(usage, description, flags.ToHashSet(StringComparer.OrdinalIgnoreCase), extraLines, ValidateFlags: true)
        {
        }

        public CommandHelpEntry(string usage, string description, IReadOnlySet<string> flags, bool ValidateFlags)
            : this(usage, description, flags, [], ValidateFlags)
        {
        }

        public static CommandHelpEntry Generic(string command) => new(
            $"Usage: {command} [options]",
            "Run this operator command.",
            GenericHelpFlags,
            [],
            ValidateFlags: false);
    }
}

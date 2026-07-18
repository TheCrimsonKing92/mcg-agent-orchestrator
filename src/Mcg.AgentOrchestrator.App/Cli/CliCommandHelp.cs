using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliCommandHelp
{
    public const string ConductUsage = "Usage: conduct <goal-id-prefix> [--policy <Conservative|Permissive|Manual>] [--watch [--poll-seconds <n>]], or conduct --loop [--max-iterations <n>] [--max-duration <seconds>] [--watch|--daemon] [--poll-seconds <n>]";
    public const string GoalUsage = "Usage: goal <objective> [--simple] [--from-backlog] [--run --confirm-batch-start] [--backlog-item <id-prefix>] | goal --brief-file <path> | goal --text-file <path>";
    public const string AddTaskUsage = "Usage: add-task <role> <description> | add-task <role> --text-file <path>";
    public const string RetryUsage = "Usage: retry <task-number> <message> [--mechanical] | retry <goal-prefix> <task-number> <message> [--mechanical] | retry --goal <goal-prefix> <task-number> <message> [--mechanical] | retry <task-number> --text-file <path> [--mechanical]";
    public const string NoteUsage = "Usage: note <task-number> <message> | note <goal-prefix> <task-number> <message> | note --goal <goal-prefix> <task-number> <message> | note <task-number> --text-file <path>";
    public const string ProgressUsage = "Usage: progress <task-number> <status> <message> | progress <task-number> <status> --text-file <path>";
    public const string VerifyManualUsage = "Usage: verify-manual <task-number> <passed|failed> <note> | verify-manual <task-number> <passed|failed> --text-file <path>";
    public const string RecoverUsage = "Usage: recover <goal-prefix> <note> | recover <goal-prefix> --text-file <path>";
    public const string AnswerUsage = "Usage: answer <request-id> <answer> | answer <request-id> --text-file <path>";
    public const string AttentionUsage = "Usage: attention show [--all|--include-parked] [--goal] <goal-id-prefix> | attention dismiss <goal-id-prefix> | attention answer [<goal-id-prefix>] <id> <answer> | attention answer [<goal-id-prefix>] <id> --text-file <path>";
    public const string AbandonGoalUsage = "Usage: abandon-goal <goal-id-prefix> <reason> [--confirm-goal-abandon] | abandon-goal <goal-id-prefix> --text-file <path> [--confirm-goal-abandon]";
    public const string CancelGoalUsage = "Usage: cancel-goal <goal-id-prefix> <reason> [--confirm-goal-stop] | cancel-goal <goal-id-prefix> --text-file <path> [--confirm-goal-stop]";
    public const string SupersedeGoalUsage = "Usage: supersede-goal <goal-id-prefix> <reason> [--confirm-goal-stop] | supersede-goal <goal-id-prefix> --text-file <path> [--confirm-goal-stop]";
    public const string ParkGoalUsage = "Usage: park-goal <goal-id-prefix> <reason> [--confirm-goal-park] | park-goal <goal-id-prefix> --text-file <path> [--confirm-goal-park]";
    public const string UnparkGoalUsage = "Usage: unpark-goal <goal-id-prefix> <reason> [--confirm-goal-unpark] | unpark-goal <goal-id-prefix> --text-file <path> [--confirm-goal-unpark]";
    public const string StopUsage = "Usage: stop <goal-id-prefix> <reason> --as cancel|park|abandon|supersede | stop <goal-id-prefix> --text-file <path> --as cancel|park|abandon|supersede";
    public const string SubscriptionDispatchUsage = "Usage: subscription-dispatch <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> [--confirm-limit-review <note>|--confirm-limit-review --text-file <path>] [--subscription-model <model>] [--subscription <profile>] [--subscription-reasoning <effort>] [--allow-git-reference]";
    public const string AgentUsage = "Usage: agent <role> <provider> <model> [name] [--complex-model <model>] [--subscription-model <model>] [--subscription-reasoning <effort>]";
    public const string AgentAddUsage = "Usage: agent-add <role> <provider> <model> [name] [--complex-model <model>] [--subscription-model <model>] [--subscription-reasoning <effort>]";
    public const string WorkspaceUsage = "Usage: workspace [create|merge|rebase|remove] [goal-id-prefix]";
    public const string WorkspaceCreateUsage = "Usage: workspace create [goal-id-prefix]";
    public const string ReassignAgentUsage = "Usage: reassign-agent <task-number> <agent-id>|<goal-prefix> <task-number> <agent-id>|--goal <goal-prefix> <task-number> <agent-id>";
    public const string BacklogListUsage = "Usage: backlog-list [--all] [--limit <n>] [--status <value>] [--text <pattern>]";
    public const string BacklogTriageUsage = "Usage: backlog-triage [--limit <n>] [--stale-days <n>]";
    public const string BacklogAddUsage = "Usage: backlog-add <title> [body] | backlog-add <title> --body-file <path> | backlog-add <title> --text-file <path>";
    public const string BacklogShowUsage = "Usage: backlog-show <id-prefix>";
    public const string BacklogAnnotateUsage = "Usage: backlog-annotate <id-prefix> <note> | backlog-annotate <id-prefix> --text-file <path>";
    public const string BacklogCloseUsage = "Usage: backlog-close <id-prefix> [reason] | backlog-close <id-prefix> --reason-file <path> | backlog-close <id-prefix> --text-file <path>";
    public const string BacklogReopenUsage = "Usage: backlog-reopen <id-prefix> [reason]";
    public const string BacklogViewUsage = "Usage: backlog-view";
    public const string DogfoodLogUsage = "Usage: dogfood-log list [--limit <n>] | dogfood-log add [goal-prefix]";
    public const string OperatorCommandsUsage = "Usage: operator-commands [--help]";
    public const string GateStatusUsage = "Usage: gate-status";
    public const string RunEventsMaintenanceUsage = "Usage: run-events-maintenance [--tick-max-age-days <days>] [--keep-tick-rows <count>] [--payload-max-bytes <bytes>] [--batch-size <rows>] [--legacy-purge-oversized-ticks] [--vacuum]";

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
        new[] { "--simple", "--from-backlog", "--run", "--confirm-batch-start", "--backlog-item", "--brief-file", "--text-file", "--help", "-h" }
            .ToHashSet(StringComparer.OrdinalIgnoreCase),
        ValidateFlags: false);

    private static readonly CommandHelpEntry GoalsSubscribe = new(
        GoalMonitoringSubscriptionCommand.GoalsSubscribeUsage,
        "Monitor goal lifecycle events. Example: goals subscribe --goal-prefix abc123 --event-kind conductor:dispatch --wait-terminal --once --timeout 5m",
        ["--goal-prefix", "--from-cursor", "--since", "--task", "--event-kind", "--once", "--wait-terminal", "--format", "--timeout", "--help", "-h"]);

    private static readonly CommandHelpEntry AddTask = new(
        AddTaskUsage,
        "Add a task to the current goal.",
        ["--text-file", "--help", "-h"]);

    private static readonly CommandHelpEntry Retry = new(
        RetryUsage,
        "Retry a task with operator feedback.",
        ["--goal", "--text-file", "--mechanical", "--autonomy", "--autonomy-policy", "--help", "-h"]);

    private static readonly CommandHelpEntry Note = new(
        NoteUsage,
        "Record a status-neutral task note.",
        ["--goal", "--text-file", "--help", "-h"]);

    private static readonly CommandHelpEntry Progress = new(
        ProgressUsage,
        "Record task progress.",
        ["--goal", "--text-file", "--help", "-h"]);

    private static readonly CommandHelpEntry VerifyManual = new(
        VerifyManualUsage,
        "Record manual verification evidence for a task.",
        ["--goal", "--text-file", "--help", "-h"]);

    private static readonly CommandHelpEntry Recover = new(
        RecoverUsage,
        "Recover stuck goal/task state with an operator note.",
        ["--text-file", "--autonomy", "--autonomy-policy", "--help", "-h"]);

    private static readonly CommandHelpEntry Answer = new(
        AnswerUsage,
        "Submit an answer to a human-input request.",
        ["--text-file", "--help", "-h"]);

    private static readonly CommandHelpEntry Attention = new(
        AttentionUsage,
        "Show, dismiss, or answer operator attention items.",
        ["--all", "--include-parked", "--goal", "--text-file", "--help", "-h"]);

    private static readonly CommandHelpEntry AbandonGoal = new(
        AbandonGoalUsage,
        "Abandon a goal after preview or confirmation.",
        ["--text-file", "--confirm-goal-abandon", "--help", "-h"]);

    private static readonly CommandHelpEntry CancelGoal = new(
        CancelGoalUsage,
        "Cancel a goal.",
        ["--text-file", "--confirm-goal-stop", "--help", "-h"]);

    private static readonly CommandHelpEntry SupersedeGoal = new(
        SupersedeGoalUsage,
        "Supersede a goal.",
        ["--text-file", "--confirm-goal-stop", "--help", "-h"]);

    private static readonly CommandHelpEntry ParkGoal = new(
        ParkGoalUsage,
        "Park a goal. Use unpark-goal to resume it later.",
        ["--text-file", "--confirm-goal-park", "--help", "-h"]);

    private static readonly CommandHelpEntry UnparkGoal = new(
        UnparkGoalUsage,
        "Resume a Parked goal.",
        ["--text-file", "--confirm-goal-unpark", "--help", "-h"]);

    private static readonly CommandHelpEntry Stop = new(
        StopUsage,
        "Route a goal stop disposition to cancel, park, abandon, or supersede.",
        ["--text-file", "--as", "--confirm-goal-stop", "--confirm-goal-park", "--confirm-goal-abandon", "--help", "-h"]);

    private static readonly CommandHelpEntry SubscriptionDispatch = new(
        SubscriptionDispatchUsage,
        "Prepare a subscription-backed task dispatch.",
        ["--goal", "--confirm-limit-review", "--text-file", "--subscription-model", "--subscription", "--subscription-reasoning", "--allow-git-reference", "--confirm-dispatch-start", "--confirm-large-paid-subscription-start", "--autonomy", "--autonomy-policy", "--confirm-readiness-risk", "--help", "-h"]);

    private static readonly CommandHelpEntry Agent = new(
        AgentUsage,
        "Replace the primary worker agent for a role.",
        ["--complex-model", "--subscription-model", "--subscription-reasoning", "--help", "-h"]);

    private static readonly CommandHelpEntry AgentAdd = new(
        AgentAddUsage,
        "Add or replace a same-role alternate worker agent by id.",
        ["--complex-model", "--subscription-model", "--subscription-reasoning", "--help", "-h"]);

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
        ["--body-file", "--text-file", "--help", "-h"]);

    private static readonly CommandHelpEntry BacklogShow = new(
        BacklogShowUsage,
        "Show a backlog item by id prefix.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry BacklogAnnotate = new(
        BacklogAnnotateUsage,
        "Append a timestamped note to a backlog item by id prefix.",
        ["--text-file", "--help", "-h"]);

    private static readonly CommandHelpEntry BacklogClose = new(
        BacklogCloseUsage,
        "Close a backlog item by id prefix.",
        ["--reason-file", "--text-file", "--help", "-h"]);

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

    private static readonly CommandHelpEntry GateStatus = new(
        GateStatusUsage,
        "List acceptance gate heartbeat status for stable build slots.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry RunEventsMaintenance = new(
        RunEventsMaintenanceUsage,
        "Prune high-churn run-events.db conductor tick rows and optionally reclaim free pages when idle.",
        ["--tick-max-age-days", "--keep-tick-rows", "--payload-max-bytes", "--batch-size", "--legacy-purge-oversized-ticks", "--vacuum", "--help", "-h"]);

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

        if (IsGoalsSubscribeHelpTarget(args))
        {
            entry = GoalsSubscribe;
            return true;
        }

        if (args[0].Equals("add-task", StringComparison.OrdinalIgnoreCase))
        {
            entry = AddTask;
            return true;
        }

        if (args[0].Equals("retry", StringComparison.OrdinalIgnoreCase))
        {
            entry = Retry;
            return true;
        }

        if (args[0].Equals("note", StringComparison.OrdinalIgnoreCase))
        {
            entry = Note;
            return true;
        }

        if (args[0].Equals("progress", StringComparison.OrdinalIgnoreCase))
        {
            entry = Progress;
            return true;
        }

        if (args[0].Equals("verify-manual", StringComparison.OrdinalIgnoreCase))
        {
            entry = VerifyManual;
            return true;
        }

        if (args[0].Equals("recover", StringComparison.OrdinalIgnoreCase))
        {
            entry = Recover;
            return true;
        }

        if (args[0].Equals("answer", StringComparison.OrdinalIgnoreCase))
        {
            entry = Answer;
            return true;
        }

        if (args[0].Equals("attention", StringComparison.OrdinalIgnoreCase))
        {
            entry = Attention;
            return true;
        }

        if (args[0].Equals("run-events-maintenance", StringComparison.OrdinalIgnoreCase))
        {
            entry = RunEventsMaintenance;
            return true;
        }

        if (args[0].Equals("abandon-goal", StringComparison.OrdinalIgnoreCase))
        {
            entry = AbandonGoal;
            return true;
        }

        if (args[0].Equals("cancel-goal", StringComparison.OrdinalIgnoreCase))
        {
            entry = CancelGoal;
            return true;
        }

        if (args[0].Equals("supersede-goal", StringComparison.OrdinalIgnoreCase))
        {
            entry = SupersedeGoal;
            return true;
        }

        if (args[0].Equals("park-goal", StringComparison.OrdinalIgnoreCase))
        {
            entry = ParkGoal;
            return true;
        }

        if (args[0].Equals("unpark-goal", StringComparison.OrdinalIgnoreCase))
        {
            entry = UnparkGoal;
            return true;
        }

        if (args[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            entry = Stop;
            return true;
        }

        if (args[0].Equals("subscription-dispatch", StringComparison.OrdinalIgnoreCase))
        {
            entry = SubscriptionDispatch;
            return true;
        }

        if (args[0].Equals("agent", StringComparison.OrdinalIgnoreCase))
        {
            entry = Agent;
            return true;
        }

        if (args[0].Equals("agent-add", StringComparison.OrdinalIgnoreCase))
        {
            entry = AgentAdd;
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

        if (args[0].Equals("backlog-annotate", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogAnnotate;
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

        if (args[0].Equals("gate-status", StringComparison.OrdinalIgnoreCase))
        {
            entry = GateStatus;
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

        var targetParts = ExpandHelpTarget(args.Skip(1)).ToArray();
        target = string.Join(' ', targetParts);
        if (TryResolveEntry(targetParts, out var resolved))
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
        return args.Any(IsHelpFlag) ||
            (IsGoalsSubscribeHelpTarget(args) && SplitCompositeToken(args[1]).Any(IsHelpFlag));
    }

    private static bool IsGoalsSubscribeHelpTarget(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !args[0].Equals("goals", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return SplitCompositeToken(args[1]).FirstOrDefault()?.Equals("subscribe", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static IEnumerable<string> ExpandHelpTarget(IEnumerable<string> targetParts)
    {
        var parts = targetParts.ToArray();
        return parts.Length == 1 ? SplitCompositeToken(parts[0]) : parts;
    }

    private static IReadOnlyList<string> SplitCompositeToken(string value) =>
        value.Contains(' ')
            ? CliArgumentParser.SplitCommand(value)
            : [value];

    private static bool IsHelpFlag(string arg) =>
        arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("-h", StringComparison.OrdinalIgnoreCase);

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

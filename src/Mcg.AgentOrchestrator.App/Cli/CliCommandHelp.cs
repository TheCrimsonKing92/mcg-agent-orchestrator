namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliCommandHelp
{
    public const string ConductUsage = "Usage: conduct <goal-id-prefix> [--policy <Conservative|Permissive|Manual>], or conduct --loop [--max-iterations <n>] [--max-duration <seconds>] [--watch|--daemon]";
    public const string WorkspaceUsage = "Usage: workspace [create|merge|rebase|remove] [goal-id-prefix]";
    public const string WorkspaceCreateUsage = "Usage: workspace create [goal-id-prefix]";
    public const string ReassignAgentUsage = "Usage: reassign-agent <task-number> <agent-id>|<goal-prefix> <task-number> <agent-id>|--goal <goal-prefix> <task-number> <agent-id>";
    public const string BacklogListUsage = "Usage: backlog-list [--all]";
    public const string BacklogAddUsage = "Usage: backlog-add <title> [body] | backlog-add <title> --body-file <path>";
    public const string BacklogShowUsage = "Usage: backlog-show <id-prefix>";
    public const string BacklogCloseUsage = "Usage: backlog-close <id-prefix> [reason] | backlog-close <id-prefix> --reason-file <path>";
    public const string BacklogReopenUsage = "Usage: backlog-reopen <id-prefix> [reason]";
    public const string BacklogViewUsage = "Usage: backlog-view";

    private static readonly CommandHelpEntry Conduct = new(
        ConductUsage,
        "Drive one goal or run the autonomous conductor loop.",
        ["--policy", "--loop", "--max-iterations", "--max-duration", "--watch", "--daemon", "--help", "-h"]);

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
        ["--all", "--help", "-h"]);

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

    private static readonly IReadOnlySet<string> GenericHelpFlags =
        new[] { "--help", "-h" }.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool TryPrintStartupHelp(IReadOnlyList<string> args)
    {
        if (!TryResolveEntry(args, out var entry) || !HasHelpFlag(args))
        {
            return false;
        }

        Print(entry);
        return true;
    }

    internal static bool IsCommandSpecificHelp(IReadOnlyList<string> args)
    {
        return HasHelpFlag(args) && TryResolveEntry(args, out _);
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
                throw new ArgumentException($"Unknown option '{arg}'. {entry.Usage}");
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

    private static void Print(CommandHelpEntry entry)
    {
        Console.WriteLine(entry.Usage);
        Console.WriteLine();
        Console.WriteLine(entry.Description);
        Console.WriteLine();
        Console.WriteLine("Options:");
        foreach (var flag in entry.Flags.Where(flag => flag.StartsWith("-", StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  {flag}");
        }
    }

    private static bool HasHelpFlag(IReadOnlyList<string> args)
    {
        return args.Any(arg =>
            arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("-h", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFlag(string arg) =>
        arg.StartsWith("-", StringComparison.Ordinal);

    private readonly record struct CommandHelpEntry(
        string Usage,
        string Description,
        IReadOnlySet<string> Flags,
        bool ValidateFlags)
    {
        public CommandHelpEntry(string usage, string description, IEnumerable<string> flags)
            : this(usage, description, flags.ToHashSet(StringComparer.OrdinalIgnoreCase), ValidateFlags: true)
        {
        }

        public static CommandHelpEntry Generic(string command) => new(
            $"Usage: {command} [options]",
            "Run this operator command.",
            GenericHelpFlags,
            ValidateFlags: false);
    }
}

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliCommandHelp
{
    public const string ConductUsage = "Usage: conduct <goal-id-prefix> [--policy <Conservative|Permissive|Manual>], or conduct --loop [--max-iterations <n>] [--max-duration <seconds>] [--watch|--daemon]";
    public const string WorkspaceUsage = "Usage: workspace [create|merge|rebase|remove] [goal-id-prefix]";
    public const string WorkspaceCreateUsage = "Usage: workspace create [goal-id-prefix]";

    public static bool TryPrintStartupHelp(IReadOnlyList<string> args)
    {
        if (!TryResolveUsage(args, out var usage))
        {
            return false;
        }

        Console.WriteLine(usage);
        return true;
    }

    internal static bool IsCommandSpecificHelp(IReadOnlyList<string> args)
    {
        return TryResolveUsage(args, out _);
    }

    private static bool TryResolveUsage(IReadOnlyList<string> args, out string usage)
    {
        usage = string.Empty;
        if (args.Count == 0 || !HasHelpFlag(args))
        {
            return false;
        }

        if (args[0].Equals("conduct", StringComparison.OrdinalIgnoreCase))
        {
            usage = ConductUsage;
            return true;
        }

        if (!args[0].Equals("workspace", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (args.Count > 1 && args[1].Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            usage = WorkspaceCreateUsage;
            return true;
        }

        usage = WorkspaceUsage;
        return true;
    }

    private static bool HasHelpFlag(IReadOnlyList<string> args)
    {
        return args.Any(arg =>
            arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("-h", StringComparison.OrdinalIgnoreCase));
    }
}

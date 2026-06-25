namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record OrchestratorProjectSelection(string? ProjectName, IReadOnlyList<string> CommandArgs)
{
    public const string ProjectEnvironmentVariable = "MCG_ORCHESTRATOR_PROJECT";

    public static OrchestratorProjectSelection FromArgs(
        IReadOnlyList<string> args,
        string? environmentProject)
    {
        var projectName = environmentProject;
        var commandArgs = new List<string>(args.Count);
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg.Equals("--project", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Count)
                {
                    throw new ArgumentException("Usage: --project <project-name> must include a project name.");
                }

                projectName = args[index + 1];
                index++;
                continue;
            }

            if (arg.StartsWith("--project=", StringComparison.OrdinalIgnoreCase))
            {
                projectName = arg["--project=".Length..];
                continue;
            }

            commandArgs.Add(arg);
        }

        return new OrchestratorProjectSelection(
            string.IsNullOrWhiteSpace(projectName) ? null : NormalizeProjectName(projectName),
            commandArgs);
    }

    public static string NormalizeProjectName(string? projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return OrchestratorWorkspace.DefaultProjectName;
        }

        var trimmed = projectName.Trim();
        if (trimmed.Length > 64)
        {
            throw new ArgumentException("Project name must be 64 characters or fewer.");
        }

        if (trimmed is "." or "..")
        {
            throw new ArgumentException("Project name cannot be a relative path segment.");
        }

        foreach (var ch in trimmed)
        {
            var allowed = char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_';
            if (!allowed)
            {
                throw new ArgumentException("Project name may contain only ASCII letters, digits, '-' and '_'.");
            }
        }

        return trimmed;
    }
}

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum Toolchain
{
    Dotnet,
    Go,
    Node,
    Python,
    Unknown
}

public static class TargetToolchainDetector
{
    // Dotnet list is unchanged from the original SourceSurveyExtensions.
    private static readonly string[] DotnetExtensions =
    [
        ".cs", ".csproj", ".sln", ".props", ".targets", ".json", ".md", ".ps1", ".cmd",
        ".razor", ".cshtml", ".html", ".css", ".js", ".ts", ".yml", ".yaml"
    ];

    private static readonly string[] GoExtensions =
    [
        ".go", ".mod", ".sum", ".json", ".md", ".yaml", ".yml", ".sh"
    ];

    private static readonly string[] NodeExtensions =
    [
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".json", ".md",
        ".yaml", ".yml", ".html", ".css", ".scss", ".sass"
    ];

    private static readonly string[] PythonExtensions =
    [
        ".py", ".pyi", ".json", ".md", ".yaml", ".yml", ".toml", ".txt", ".sh"
    ];

    // Unknown toolchain: include a comprehensive catch-all so undetected repos remain
    // fully indexed. The toolchain-specific lists above intentionally narrow scope.
    private static readonly string[] UnknownExtensions =
    [
        ".cs", ".go", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".py", ".pyi",
        ".rb", ".java", ".rs", ".cpp", ".c", ".h", ".hpp", ".swift", ".kt",
        ".csproj", ".sln", ".props", ".targets", ".razor", ".cshtml",
        ".json", ".md", ".yaml", ".yml", ".toml", ".txt", ".sh", ".bat", ".ps1", ".cmd",
        ".html", ".css", ".scss", ".sass"
    ];

    /// <summary>
    /// Priority order: go.mod → Go; package.json → Node; *.sln/*.csproj → Dotnet;
    /// pyproject.toml/requirements.txt → Python; else Unknown.
    /// </summary>
    public static Toolchain Detect(string workingDirectory)
    {
        if (File.Exists(Path.Combine(workingDirectory, "go.mod")))
        {
            return Toolchain.Go;
        }

        if (File.Exists(Path.Combine(workingDirectory, "package.json")))
        {
            return Toolchain.Node;
        }

        if (HasSlnOrCsproj(workingDirectory))
        {
            return Toolchain.Dotnet;
        }

        if (File.Exists(Path.Combine(workingDirectory, "pyproject.toml")) ||
            File.Exists(Path.Combine(workingDirectory, "requirements.txt")))
        {
            return Toolchain.Python;
        }

        return Toolchain.Unknown;
    }

    public static string[] GetSourceExtensions(Toolchain toolchain) => toolchain switch
    {
        Toolchain.Dotnet => DotnetExtensions,
        Toolchain.Go => GoExtensions,
        Toolchain.Node => NodeExtensions,
        Toolchain.Python => PythonExtensions,
        _ => UnknownExtensions
    };

    public static string GetBrokerCommandHint(Toolchain toolchain, string goalPrefix, string attemptName) =>
        toolchain switch
        {
            Toolchain.Dotnet =>
                $".\\scripts\\Invoke-IsolatedDotnet.ps1 -GoalPrefix {goalPrefix} -AttemptName {attemptName} test <project-or-sln> --verbosity minimal",
            Toolchain.Go => "go test ./...",
            Toolchain.Node => "npm test (or package.json test script)",
            Toolchain.Python => "python -m pytest (or project test command)",
            _ => "run the project's test suite"
        };

    public static string GetBrokerBuildTestNote(Toolchain toolchain) => toolchain switch
    {
        Toolchain.Dotnet => "avoiding raw unisolated `dotnet test`",
        Toolchain.Go => "running `go test ./...` or focused package tests",
        Toolchain.Node => "running `npm test` or package.json scripts",
        Toolchain.Python => "running `python -m pytest` or project test commands",
        _ => "running the project's build and test commands"
    };

    private static bool HasSlnOrCsproj(string workingDirectory)
    {
        try
        {
            // .sln files are nearly always at the repository root.
            if (Directory.EnumerateFiles(workingDirectory, "*.sln", SearchOption.TopDirectoryOnly).Any())
            {
                return true;
            }

            // .csproj files live up to two levels deep (e.g. src/ProjectName/Project.csproj).
            foreach (var level1 in Directory.EnumerateDirectories(workingDirectory))
            {
                if (Directory.EnumerateFiles(level1, "*.sln", SearchOption.TopDirectoryOnly).Any() ||
                    Directory.EnumerateFiles(level1, "*.csproj", SearchOption.TopDirectoryOnly).Any())
                {
                    return true;
                }

                foreach (var level2 in Directory.EnumerateDirectories(level1))
                {
                    if (Directory.EnumerateFiles(level2, "*.csproj", SearchOption.TopDirectoryOnly).Any())
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}

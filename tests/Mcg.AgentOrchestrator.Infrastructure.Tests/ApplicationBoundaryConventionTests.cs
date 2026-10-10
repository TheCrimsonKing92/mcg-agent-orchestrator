using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

/// <summary>
/// Enforces the dependency direction the application module exists to establish: presentation adapts
/// application operations, and goal execution never reaches back into presentation. The module lives
/// in the headless application assembly, while the dashboard host references it one way. Source
/// inspection complements that project-level compile boundary for the application module itself.
/// </summary>
public sealed class ApplicationBoundaryConventionTests
{
    private const string ApplicationDirectory = "src/Mcg.AgentOrchestrator.App/Application";

    private static readonly string[] ForbiddenUsingNamespaces =
    [
        "Mcg.AgentOrchestrator.App.Dashboard",
        "Mcg.AgentOrchestrator.App.Rendering",
        "Mcg.AgentOrchestrator.App.Cli",
        "Microsoft.AspNetCore"
    ];

    private static readonly string[] ForbiddenTransportIdentifiers =
    [
        "HttpContext",
        "IResult",
        "IActionResult",
        "StatusCodes",
        "DashboardRequestParser",
        "DashboardResponseMapper"
    ];

    private static readonly string[] RequiredOwners =
    [
        "GoalDispatchOperations",
        "GoalAdvancementOperations",
    ];

    [Xunit.Fact(DisplayName = "Application_module_exists_and_declares_its_operation_owners")]
    public void ApplicationModuleExistsAndDeclaresItsOperationOwners()
    {
        var files = LoadApplicationSources();

        // Guards against a vacuous pass: a deleted or empty directory must fail, not succeed.
        Assert.NotEmpty(files);
        foreach (var owner in RequiredOwners)
        {
            Assert.True(
                files.Any(file => file.Text.Contains($"class {owner}", StringComparison.Ordinal)),
                $"Expected the application module to declare '{owner}'.");
        }
    }

    [Xunit.Fact(DisplayName = "Application_module_does_not_reference_presentation_or_http")]
    public void ApplicationModuleDoesNotReferencePresentationOrHttp()
    {
        var violations = new List<string>();
        foreach (var file in LoadApplicationSources())
        {
            foreach (var line in file.Text.ReplaceLineEndings("\n").Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("using ", StringComparison.Ordinal) &&
                    ForbiddenUsingNamespaces.Any(ns => trimmed.Contains(ns, StringComparison.Ordinal)))
                {
                    violations.Add($"{file.RelativePath}: forbidden using '{trimmed}'");
                }

                if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                    trimmed.StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var identifier in ForbiddenTransportIdentifiers)
                {
                    if (Regex.IsMatch(trimmed, $@"\b{Regex.Escape(identifier)}\b"))
                    {
                        violations.Add($"{file.RelativePath}: transport identifier '{identifier}' in '{trimmed}'");
                    }
                }

                if (Regex.IsMatch(trimmed, @"\b\w*Dto\b"))
                {
                    violations.Add($"{file.RelativePath}: dashboard DTO reference in '{trimmed}'");
                }

                if (Regex.IsMatch(trimmed, @"\bDashboard\w*\b"))
                {
                    violations.Add($"{file.RelativePath}: dashboard type reference in '{trimmed}'");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Xunit.Fact(DisplayName = "Application_module_does_not_delegate_back_to_presentation_through_callbacks")]
    public void ApplicationModuleDoesNotDelegateBackToPresentationThroughCallbacks()
    {
        var violations = new List<string>();
        foreach (var file in LoadApplicationSources())
        {
            foreach (var (declaration, arguments) in FindDelegateTypeArguments(file.Text))
            {
                if (Regex.IsMatch(arguments, @"\bDashboard\w*\b") || Regex.IsMatch(arguments, @"\b\w*Dto\b"))
                {
                    violations.Add($"{file.RelativePath}: delegate parameter forwards to presentation: '{declaration}'");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// Yields the full type-argument list of every <c>Func&lt;...&gt;</c>/<c>Action&lt;...&gt;</c> by
    /// matching angle brackets by depth. A character-class scan stops at the first '&gt;', so a
    /// forwarding delegate nested one level deep - <c>Func&lt;Goal, Func&lt;TaskSpec, SomeDto&gt;&gt;</c> -
    /// would escape the guard entirely.
    /// </summary>
    private static IEnumerable<(string Declaration, string Arguments)> FindDelegateTypeArguments(string text)
    {
        foreach (Match open in Regex.Matches(text, @"\b(Func|Action)<"))
        {
            var start = open.Index + open.Length;
            var depth = 1;
            var index = start;
            while (index < text.Length && depth > 0)
            {
                if (text[index] == '<')
                {
                    depth++;
                }
                else if (text[index] == '>')
                {
                    depth--;
                }

                index++;
            }

            if (depth != 0)
            {
                continue;
            }

            var arguments = text[start..(index - 1)];
            yield return ($"{open.Value}{arguments}>", arguments);
        }
    }

    [Xunit.Fact(DisplayName = "Delegate_forwarding_guard_inspects_nested_generic_arguments")]
    public void DelegateForwardingGuardInspectsNestedGenericArguments()
    {
        // Negative control for the guard itself: the previous character-class scan reported no
        // arguments for this shape, so the guard would have passed a nested forwarding delegate.
        var nested = FindDelegateTypeArguments("private Func<Goal, Func<TaskSpec, SomeDto>> _render;").ToList();

        Assert.Equal(2, nested.Count);
        Assert.Contains(nested, candidate => Regex.IsMatch(candidate.Arguments, @"\b\w*Dto\b"));
    }

    [Xunit.Fact(DisplayName = "Dashboard_goal_command_service_no_longer_owns_dispatch_execution")]
    public void DashboardGoalCommandServiceNoLongerOwnsDispatchExecution()
    {
        var repositoryRoot = FindRepositoryRoot();
        Assert.False(
            File.Exists(Path.Combine(
                repositoryRoot,
                "src",
                "Mcg.AgentOrchestrator.App",
                "Dashboard",
                "Api",
                "GoalManagementCommandService.Dispatches.cs")),
            "Dispatch execution must not survive under the dashboard owner.");
    }

    private sealed record ApplicationSource(string RelativePath, string Text);

    private static IReadOnlyList<ApplicationSource> LoadApplicationSources()
    {
        var repositoryRoot = FindRepositoryRoot();
        var directory = Path.Combine(repositoryRoot, ApplicationDirectory.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(directory), $"Expected the application module directory at '{directory}'.");

        return Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => new ApplicationSource(
                Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'),
                File.ReadAllText(path)))
            .ToList();
    }

    // Derived from this test's own source location, never from Environment.CurrentDirectory, so the
    // guard inspects the worktree it was compiled from even under an isolated artifacts root.
    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) &&
                File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repository root from source file path '{sourceFilePath}'.");
    }
}

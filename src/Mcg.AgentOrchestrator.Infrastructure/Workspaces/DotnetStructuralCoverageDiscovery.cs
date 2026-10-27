using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using StructuralCoverageDeclarationPlan = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.StructuralCoverageDeclarationPlan;

namespace Mcg.AgentOrchestrator.Infrastructure;

// The one place the gate chooses the .NET shape of structural coverage checks and test discovery commands.
internal static class DotnetStructuralCoverageDiscovery
{
    internal static StructuralCoverageDeclarationPlan EnsureDeclarations(
        IReadOnlyList<AcceptanceManifestCheck> effectiveChecks,
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks,
        string worktreePath)
    {
        var completed = effectiveChecks.ToList();
        var undeclared = new List<string>();
        foreach (var project in AcceptanceStructuralCoverageInputs.DiscoverTrustedTestProjects(worktreePath))
        {
            var declarations = manifestChecks.Where(check =>
                check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(AcceptancePolicyShardPlanner.NormalizePath(check.Project), project, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (declarations.Length == 0)
            {
                undeclared.Add(project);
                continue;
            }

            if (completed.Any(check =>
                    IsBroadTestProjectCheck(check) &&
                    string.Equals(AcceptancePolicyShardPlanner.NormalizePath(check.Project), project, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (declarations.FirstOrDefault(IsBroadTestProjectCheck) is { } umbrella)
                completed.Add(umbrella);
        }

        return new StructuralCoverageDeclarationPlan(completed, undeclared);
    }

    internal static bool IsBroadTestProjectCheck(AcceptanceManifestCheck check) =>
        check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(check.Project) &&
        check.Project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
        !check.Arguments.Any(argument => argument.Equals("--filter", StringComparison.OrdinalIgnoreCase));

    internal static AcceptanceManifestCheck BuildTrustedCheck(
        string worktreePath,
        string project,
        IReadOnlyList<AcceptanceManifestCheck> effectiveChecks)
    {
        var matchingChecks = effectiveChecks
            .Where(check =>
                check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(AcceptancePolicyShardPlanner.NormalizePath(check.Project), project, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matchingChecks.FirstOrDefault(IsBroadTestProjectCheck) is { } broadCheck)
        {
            return broadCheck;
        }

        if (matchingChecks.FirstOrDefault() is { } filteredCheck)
        {
            var laneSeparator = filteredCheck.Name.LastIndexOf(": ", StringComparison.Ordinal);
            return new AcceptanceManifestCheck
            {
                Name = laneSeparator > 0 ? filteredCheck.Name[..laneSeparator] : filteredCheck.Name,
                Type = filteredCheck.Type,
                Project = filteredCheck.Project,
                Arguments = RemoveTestFilter(filteredCheck.Arguments),
                TimeoutMinutes = filteredCheck.TimeoutMinutes,
                Runner = filteredCheck.Runner
            };
        }

        return new AcceptanceManifestCheck
        {
            Name = $"trusted structural discovery: {Path.GetFileNameWithoutExtension(project)}",
            Type = "dotnet-test",
            Project = project,
            Runner = DotnetTestCheckTranslator.ResolveRunner(worktreePath, project)
        };
    }

    internal static IReadOnlyList<string> RemoveTestFilter(IReadOnlyList<string> arguments)
    {
        var unfiltered = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Equals("--filter", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            unfiltered.Add(arguments[index]);
        }

        return unfiltered;
    }

    internal static string[] BuildDiscoveryArguments(
        AcceptanceManifestCheck check,
        AcceptanceGateEngineSettings engineSettings,
        DotnetBuildEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(check.Project))
        {
            throw new InvalidDataException($"Acceptance check '{check.Name}' has no discovery project.");
        }

        if (AcceptanceCheckCommandBuilder.UsesMicrosoftTestingPlatform(check))
        {
            var invocation = engineSettings.ResolveMtpInvocation(check.Project);
            // Discovery must mirror the execution-side unattended exclusion: lanes filter out
            // Category=HostIntegration tests, so listing them here would make the structural
            // coverage invariant report by-design-excluded tests as missing on every attempt.
            return AcceptanceCheckCommandBuilder.UseDotnetHostForManagedExecutable(
            [
                invocation.ResolveManagedAssemblyPath(environment),
                "--no-ansi",
                "--progress",
                "off",
                "--list-tests",
                "json",
                "--filter-not-trait",
                "Category=HostIntegration"
            ]);
        }

        var arguments = new List<string>
        {
            "dotnet",
            "test",
            check.Project,
            "--no-build",
            "--list-tests",
            "--filter",
            "Category!=HostIntegration"
        };
        return AcceptanceCheckCommandBuilder.WithBuildEnvironmentArguments([.. arguments], environment);
    }

    internal static string[] BuildUnattendedDiscoveryArguments(
        AcceptanceManifestCheck check,
        AcceptanceGateEngineSettings engineSettings,
        DotnetBuildEnvironment environment)
    {
        var arguments = BuildDiscoveryArguments(check, engineSettings, environment).ToList();
        if (AcceptanceCheckCommandBuilder.UsesMicrosoftTestingPlatform(check))
        {
            arguments.Add("--filter-not-trait");
            arguments.Add("Category=AcceptanceOptIn");
        }
        else
        {
            var filterIndex = arguments.IndexOf("--filter");
            if (filterIndex < 0 || filterIndex == arguments.Count - 1)
            {
                throw new InvalidDataException(
                    $"Acceptance check '{check.Name}' produced no discovery filter.");
            }

            arguments[filterIndex + 1] += "&Category!=AcceptanceOptIn";
        }

        return [.. arguments];
    }
}

using System.Text.RegularExpressions;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using DotnetTestTelemetry = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.DotnetTestTelemetry;
using FocusedEvidenceFilterToken = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceFilterToken;
using FocusedEvidenceTokenKind = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceTokenKind;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceCheckCommandBuilder
{
    internal static bool IsDotnetCommand(string[] arguments) =>
        arguments.Length > 0 && arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    internal static bool IsDotnetTestCommand(string[] arguments) =>
        arguments.Length >= 2 &&
        arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
        arguments[1].Equals("test", StringComparison.OrdinalIgnoreCase);

    internal static string[] UseDotnetHostForManagedExecutable(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && arguments[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? ["dotnet", .. arguments]
            : [.. arguments];

    internal static IEnumerable<string> TranslateMtpFilter(string filter) =>
        TranslateMtpFilter(filter, allowExactClassSelectors: false);

    internal static IEnumerable<string> TranslateResolvedLaneFilter(string filter) =>
        TranslateMtpFilter(filter, allowExactClassSelectors: true);

    private static IEnumerable<string> TranslateMtpFilter(string filter, bool allowExactClassSelectors)
    {
        foreach (var rawToken in Regex.Split(filter, @"[&|]"))
        {
            // A focused filter can be a parenthesized disjunction such as
            // "(FullyQualifiedName~A|FullyQualifiedName~B)|(FullyQualifiedName~C&Category!=X)".
            // MTP unions repeated --filter-class and intersects --filter-not-class/--filter-not-trait
            // exclusions, so the grouping parens carry no additional meaning at the token level and
            // are stripped before matching. (A per-group Category!= therefore widens to the whole
            // union, which is harmless because such traits are unique to a single mapped class.)
            var token = rawToken.Trim().Trim('(', ')').Trim();
            if (token.Length == 0)
            {
                continue;
            }

            var fullyQualifiedName = Regex.Match(
                token,
                @"^FullyQualifiedName\s*(?<op>!~|~|!=|=)\s*(?<value>[A-Za-z_][A-Za-z0-9_.+]*)$",
                RegexOptions.IgnoreCase);
            if (fullyQualifiedName.Success)
            {
                var op = fullyQualifiedName.Groups["op"].Value;
                if (!allowExactClassSelectors && op is "=" or "!=")
                    throw new InvalidOperationException($"MTP test filter '{filter}' contains unsupported token '{token}'.");
                yield return op is "!~" or "!="
                    ? "--filter-not-class"
                    : "--filter-class";
                var value = fullyQualifiedName.Groups["value"].Value;
                yield return op is "!~" or "~" ? $"*{value}*" : value;
                continue;
            }

            var categoryExclusion = Regex.Match(
                token,
                @"^Category\s*!=\s*(?<value>[A-Za-z_][A-Za-z0-9_.-]*)$",
                RegexOptions.IgnoreCase);
            if (categoryExclusion.Success)
            {
                yield return "--filter-not-trait";
                yield return $"Category={categoryExclusion.Groups["value"].Value}";
                continue;
            }

            throw new InvalidOperationException($"MTP test filter '{filter}' contains unsupported token '{token}'.");
        }
    }

    internal static string[] WithBuildEnvironmentArguments(
        string[] arguments,
        DotnetBuildEnvironment environment)
    {
        // The build-environment arguments are MSBuild-shaped (--artifacts-path, -maxcpucount,
        // -p:BuildInParallel=false) and only mean something to dotnet-driven commands. Appending them to a
        // direct MTP test-executable invocation corrupts the run: every post-landing gate produced an empty
        // <TestRun /> core-tests receipt and failed structural coverage (backlog 8af9b957, 2026-07-25).
        if (arguments.Length == 0 ||
            !arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            arguments.Length >= 2 && arguments[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return arguments;
        }

        return [.. arguments, .. environment.Arguments];
    }

    internal static string[] BuildDotnetTestArguments(AcceptanceManifestCheck check, bool noBuild = false)
    {
        var args = new List<string> { "dotnet", "test" };
        if (!string.IsNullOrWhiteSpace(check.Project))
        {
            args.Add(check.Project);
        }

        var explicitFilter = ExtractFilterArguments(check.Arguments, args);

        // Exclude host-integration tests from unattended gates by their
        // [Trait("Category","HostIntegration")] tag.
        if (string.IsNullOrWhiteSpace(explicitFilter) && NeedsUnattendedHostIntegrationExclusion(check))
        {
            args.Add("--filter");
            args.Add("Category!=HostIntegration");
        }

        // Fail a hung test fast and by name before the whole check budget is exhausted. A test that
        // spawns a process which blocks (e.g. on a firewall prompt) and then WaitForExit()s on it
        // can otherwise stall the whole acceptance until the configured command timeout. The
        // inactivity timeout is per-test and distinct from the full check budget.
        args.Add("--blame-hang-timeout");
        args.Add("120s");
        args.Add("--blame-hang-dump-type");
        args.Add("none");
        if (noBuild && !args.Any(argument => argument.Equals("--no-build", StringComparison.OrdinalIgnoreCase)))
        {
            args.Add("--no-build");
        }

        return [.. args];
    }

    internal static string[] BuildDotnetTestBuildArguments(AcceptanceManifestCheck check)
    {
        var testArguments = BuildDotnetTestArguments(check);
        return BuildDotnetTestBuildArguments(testArguments);
    }

    internal static string[] BuildDotnetTestBuildArguments(string[] testArguments)
    {
        var args = new List<string> { "dotnet", "build" };
        var startIndex = 2;
        if (testArguments.Length > 2 && !testArguments[2].StartsWith("-", StringComparison.Ordinal))
        {
            args.Add(testArguments[2]);
            startIndex = 3;
        }

        for (var index = startIndex; index < testArguments.Length; index++)
        {
            var argument = testArguments[index];
            if (argument.Equals("--filter", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--logger", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--collect", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--blame-hang-timeout", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--blame-hang-dump-type", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            if (argument.Equals("--no-build", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--blame", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsBuildCompatibleDotnetArgument(argument))
            {
                continue;
            }

            args.Add(argument);
            if (ArgumentExpectsValue(argument) && index + 1 < testArguments.Length)
            {
                args.Add(testArguments[++index]);
            }
        }

        return [.. args];
    }

    internal static bool NeedsUnattendedHostIntegrationExclusion(AcceptanceManifestCheck check) =>
        string.IsNullOrWhiteSpace(check.Project) ||
        check.Project.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        ProjectMatches(check.Project, InfrastructureTestsProject) ||
        IsExtractedInfrastructureProject(check.Project);

    internal static bool UsesMicrosoftTestingPlatform(AcceptanceManifestCheck check) =>
        check.Runner?.Equals("mtp", StringComparison.OrdinalIgnoreCase) == true;

    internal static bool UsesVstestRunner(AcceptanceManifestCheck check) =>
        string.IsNullOrWhiteSpace(check.Runner) ||
        check.Runner.Equals("vstest", StringComparison.OrdinalIgnoreCase);

    internal static string[] EnsureDotnetTestNoBuildArguments(string[] arguments)
    {
        if (arguments.Any(argument => argument.Equals("--no-build", StringComparison.OrdinalIgnoreCase)))
        {
            return arguments;
        }

        return [.. arguments, "--no-build"];
    }

    internal static bool IsBuildCompatibleDotnetArgument(string argument) =>
        argument.Equals("--configuration", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-c", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--framework", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-f", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--runtime", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-r", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--verbosity", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-v", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--no-restore", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--nologo", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--force", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--interactive", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("-p:", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("/p:", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("--property:", StringComparison.OrdinalIgnoreCase);

    internal static bool ArgumentExpectsValue(string argument) =>
        argument.Equals("--configuration", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-c", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--framework", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-f", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--runtime", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-r", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("--verbosity", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-v", StringComparison.OrdinalIgnoreCase);

    internal static string? ExtractFilterArguments(IReadOnlyList<string> sourceArguments, List<string> destinationArguments)
    {
        string? filter = null;
        for (var index = 0; index < sourceArguments.Count; index++)
        {
            var argument = sourceArguments[index];
            if (argument.Equals("--filter", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < sourceArguments.Count)
                {
                    filter = sourceArguments[index + 1];
                    index++;
                }

                continue;
            }

            destinationArguments.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            destinationArguments.Add("--filter");
            destinationArguments.Add(filter);
        }

        return filter;
    }

    internal static string[] BuildMtpTestArguments(
        AcceptanceManifestCheck check,
        AcceptanceGateEngineSettings settings,
        DotnetBuildEnvironment environment,
        DotnetTestTelemetry telemetry,
        bool excludeHostIntegration,
        Func<AcceptanceManifestCheck, string, IEnumerable<string>> translateCheckFilter)
    {
        if (string.IsNullOrWhiteSpace(check.Project))
        {
            throw new InvalidDataException($"Acceptance check '{check.Name}' uses MTP but has no project.");
        }

        var invocation = settings.ResolveMtpInvocation(check.Project);
        var managedAssemblyPath = invocation.ResolveManagedAssemblyPath(environment);
        var resultsDirectory = Path.GetDirectoryName(telemetry.Paths[0])
            ?? Path.Combine(environment.ArtifactsPath, "TestResults");
        var trxFileName = Path.GetFileName(telemetry.Paths[0]);
        var args = invocation.Arguments
            .Select(argument => argument
                .Replace("{executable}", managedAssemblyPath, StringComparison.Ordinal)
                .Replace("{resultsDirectory}", resultsDirectory, StringComparison.Ordinal)
                .Replace("{trxFileName}", trxFileName, StringComparison.Ordinal))
            .ToList();
        var filter = ExtractMtpCompatibleArguments(check.Arguments, args);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            args.AddRange(check.FocusedEvidenceTokens.Count > 0
                ? TranslateFocusedEvidenceTokens(check.FocusedEvidenceTokens)
                : translateCheckFilter(check, filter));
        }

        // MTP execution does not go through BuildDotnetTestArguments. Unattended acceptance /
        // full-suite checks often have no --filter in Arguments, so HostIntegration must be
        // excluded here to match discovery.
        if (excludeHostIntegration &&
            !HasMtpTraitExclusion(args, "Category=HostIntegration"))
        {
            args.Add("--filter-not-trait");
            args.Add("Category=HostIntegration");
        }

        return UseDotnetHostForManagedExecutable(args);
    }

    internal static bool HasMtpTraitExclusion(IReadOnlyList<string> arguments, string trait)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals("--filter-not-trait", StringComparison.OrdinalIgnoreCase) &&
                arguments[index + 1].Equals(trait, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static string? ExtractMtpCompatibleArguments(IReadOnlyList<string> sourceArguments, List<string> destinationArguments)
    {
        string? filter = null;
        for (var index = 0; index < sourceArguments.Count; index++)
        {
            var argument = sourceArguments[index];
            if (argument.Equals("--filter", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < sourceArguments.Count)
                {
                    filter = sourceArguments[++index];
                }

                continue;
            }

            if (argument.Equals("--verbosity", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-v", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--configuration", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-c", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--framework", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-f", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--logger", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--results-directory", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--blame-hang-timeout", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--blame-hang-dump-type", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            if (argument.Equals("--no-build", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--no-restore", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--nologo", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("-p:", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("/p:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            destinationArguments.Add(argument);
        }

        return filter;
    }

    private static IEnumerable<string> TranslateFocusedEvidenceTokens(
        IReadOnlyList<FocusedEvidenceFilterToken> tokens)
    {
        foreach (var token in tokens)
        {
            yield return token.Kind switch
            {
                FocusedEvidenceTokenKind.Class => "--filter-class",
                FocusedEvidenceTokenKind.Method => "--filter-method",
                FocusedEvidenceTokenKind.ExcludedClass => "--filter-not-class",
                FocusedEvidenceTokenKind.ExcludedTrait => "--filter-not-trait",
                _ => throw new InvalidOperationException(
                    $"Unsupported focused evidence token kind '{token.Kind}'.")
            };
            yield return token.Kind == FocusedEvidenceTokenKind.ExcludedTrait
                ? $"Category={token.Value}"
                : $"*{token.Value}*";
        }
    }

}

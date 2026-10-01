using ArchUnitNET.Domain;
using ArchUnitNET.Domain.Dependencies;
using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Conditions;
using ArchUnitNET.Fluent.Extensions;
using ArchUnitNET.Loader;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.RegularExpressions;
using Architecture = ArchUnitNET.Domain.Architecture;
using ReflectionAssembly = System.Reflection.Assembly;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

public sealed class RuntimeAssemblyArchitectureTests
{
    private static readonly ReflectionAssembly CoreAssembly = typeof(AgentOrchestratorKernel).Assembly;
    private static readonly ReflectionAssembly InfrastructureAssembly = typeof(GoalAcceptanceVerifier).Assembly;
    private static readonly ReflectionAssembly ProvidersAssembly = typeof(ProviderSmokeTester).Assembly;
    private static readonly ReflectionAssembly OperatorCommsAssembly = typeof(DiscordDecisionApplier).Assembly;
    private static readonly ReflectionAssembly AppAssembly = typeof(SubscriptionPlanBuilder).Assembly;
    private static readonly ReflectionAssembly[] RuntimeAssemblies =
        [CoreAssembly, InfrastructureAssembly, ProvidersAssembly, OperatorCommsAssembly, AppAssembly];

    // Parallel-safe: immutable assembly inputs and one published, shared architecture; no external I/O seam.
    private static int architectureLoadCount;
    private static readonly Lazy<Architecture> SharedArchitecture = new(() =>
    {
        Interlocked.Increment(ref architectureLoadCount);
        return new ArchLoader().LoadAssemblies(RuntimeAssemblies).Build();
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed record SanctionedCaller(string FullName, string Reason, string Source);

    private static readonly SanctionedCaller[] ProcessStartAllowlist =
    [
        new("Mcg.AgentOrchestrator.App.Cli.CliCommandHandlers", "Stable-slot child for CLI verbs", "src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.System.cs:1567"),
        new("Mcg.AgentOrchestrator.App.Cli.SystemConductorProcessLauncher", "Conductor start script behind IConductorProcessLauncher", "src/Mcg.AgentOrchestrator.App/Cli/CliConductorSeams.cs:38"),
        new("Mcg.AgentOrchestrator.App.Orchestration.SystemConductorSupervisorProcessHost", "Supervised conductor child", "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorContinuitySupervisor.cs:839"),
        new("Mcg.AgentOrchestrator.App.Orchestration.DotnetDumpConductorDiagnosticDumpCapture", "dotnet-dump capture of a frozen conductor", "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDiagnosticDumpCapture.cs:90"),
        new("Mcg.AgentOrchestrator.App.Orchestration.ConductorDriver", "dotnet build-server shutdown", "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs:5228"),
        new("Mcg.AgentOrchestrator.App.Orchestration.ConductorLoopHandoff", "Detached conduct loop successor", "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorLoopHandoff.cs:870"),
        new("Mcg.AgentOrchestrator.App.Orchestration.ConductorSelfRelaunch", "Successor preparation commands", "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorSelfRelaunch.cs:367"),
        new("Mcg.AgentOrchestrator.Infrastructure.BackgroundDispatchRunner", "Default start delegate, method group", "src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs:125"),
        new("Mcg.AgentOrchestrator.Infrastructure.DispatchProcessHost", "where.exe resolution and low-integrity preflight", "src/Mcg.AgentOrchestrator.Infrastructure/Processes/DispatchProcessHost.cs:856,956"),
        new("Mcg.AgentOrchestrator.Infrastructure.GitCli", "Shared git runner", "src/Mcg.AgentOrchestrator.Infrastructure/Processes/GitCli.cs:111"),
        new("Mcg.AgentOrchestrator.Infrastructure.LockAttribution", "Handle probe for build-lock attribution", "src/Mcg.AgentOrchestrator.Infrastructure/Processes/LockAttribution.cs:462"),
        new("Mcg.AgentOrchestrator.Infrastructure.ProcessTreeGuiSuppression", "Window-suppressing launcher seam", "src/Mcg.AgentOrchestrator.Infrastructure/Processes/ProcessTreeGuiSuppression.cs:185,213"),
        new("Mcg.AgentOrchestrator.Infrastructure.WorkerProcessJobs", "taskkill tree fallback", "src/Mcg.AgentOrchestrator.Infrastructure/Processes/WorkerProcessJobs.cs:2396"),
        new("Mcg.AgentOrchestrator.Infrastructure.WorkerProcessRunner", "Buffered and streaming worker runs", "src/Mcg.AgentOrchestrator.Infrastructure/Processes/WorkerProcessRunner.cs:64,130"),
        new("Mcg.AgentOrchestrator.Infrastructure.IcaclsIntegrityLabeler", "Default start delegate for icacls, method group", "src/Mcg.AgentOrchestrator.Infrastructure/Processes/WorkerSandboxPreparer.cs:490"),
        new("Mcg.AgentOrchestrator.Infrastructure.WorktreeTreeDigest", "Git tree digest", "src/Mcg.AgentOrchestrator.Infrastructure/Processes/WorktreeTreeDigest.cs:71"),
        new("Mcg.AgentOrchestrator.Infrastructure.AcceptanceGitTextResolver", "Git text reads for acceptance", "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceGitTextResolver.cs:26"),
        new("Mcg.AgentOrchestrator.Infrastructure.AcceptanceLaneClosureHasher", "Git reads for lane closure hashing", "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceLaneClosureHasher.cs:117"),
        new("Mcg.AgentOrchestrator.Infrastructure.DotnetBuildEnvironmentManager", "Build-server shutdown", "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/DotnetBuildEnvironmentManager.cs:596"),
        new("Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier", "Git scalar reads", "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs:6470"),
        new("Mcg.AgentOrchestrator.Infrastructure.GoalWorktrees", "Build-server shutdown and direct git", "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalWorktrees.Cleanup.cs:1073; src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalWorktrees.GitOps.cs:372"),
        new("Mcg.AgentOrchestrator.Infrastructure.WindowsSandboxAclHelper", "icacls sandbox reset", "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalWorktrees.cs:247")
    ];

    // ProcessStartAllowlistDifferences: none. The empty-allowlist control must prove all 22 entries.
    // If the IL model omits a method-group reference, record its type, source and cause here before removing it.

    [Fact]
    public void Architecture_loads_all_five_assemblies_once()
    {
        var architecture = SharedArchitecture.Value;
        Assert.Same(architecture, SharedArchitecture.Value);
        Assert.Equal(1, Volatile.Read(ref architectureLoadCount));
        string[] expected =
        [
            "Mcg.AgentOrchestrator.Core", "Mcg.AgentOrchestrator.Infrastructure",
            "Mcg.AgentOrchestrator.Infrastructure.Providers",
            "Mcg.AgentOrchestrator.Infrastructure.OperatorComms", "Mcg.AgentOrchestrator.App"
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal),
            RuntimeAssemblies.Select(assembly => assembly.GetName().Name!).Order(StringComparer.Ordinal));
        foreach (var assembly in RuntimeAssemblies)
        {
            Assert.True(architecture.Types.Any(type => type.Assembly.FullName == assembly.FullName),
                $"No architecture type loaded from {assembly.FullName}");
        }
    }

    [Fact]
    public void Core_has_no_outward_runtime_dependencies() => AssertPasses(LayeringRule(
        [CoreAssembly], [InfrastructureAssembly, ProvidersAssembly, OperatorCommsAssembly, AppAssembly]));

    [Fact]
    public void Core_layering_control_detects_App_dependency_on_Infrastructure() =>
        AssertNamesSubject(Failures(LayeringRule([AppAssembly], [InfrastructureAssembly])), AppAssembly);

    [Fact]
    public void Infrastructure_assemblies_do_not_depend_on_App() => AssertPasses(LayeringRule(
        [InfrastructureAssembly, ProvidersAssembly, OperatorCommsAssembly], [AppAssembly]));

    [Fact]
    public void Infrastructure_layering_control_detects_dependency_on_Core() =>
        AssertNamesSubject(Failures(LayeringRule([InfrastructureAssembly], [CoreAssembly])), InfrastructureAssembly);

    [Fact]
    public void Core_is_isolated_from_process_network_and_SQLite_runtime() =>
        AssertPasses(RuntimeIsolationRule([CoreAssembly]));

    [Fact]
    public void Runtime_isolation_control_detects_each_forbidden_family()
    {
        var infrastructureFailures = Failures(RuntimeIsolationRule([InfrastructureAssembly]));
        AssertNames(infrastructureFailures, "Microsoft.Data.Sqlite.");
        AssertNamesExactType(infrastructureFailures, "System.Diagnostics.Process");
        AssertNamesExactType(infrastructureFailures, "System.Diagnostics.ProcessStartInfo");
        AssertNamesExactType(Failures(RuntimeIsolationRule([ProvidersAssembly])), "System.Net.Http.HttpClient");
    }

    [Fact]
    public void Process_Start_callers_are_sanctioned() =>
        AssertPasses(SanctionedProcessStartRule(ProcessStartAllowlist.Select(entry => entry.FullName)));

    [Fact]
    public void Process_Start_control_detects_GitCli_and_rejects_stale_allowlist_entries()
    {
        const string gitCli = "Mcg.AgentOrchestrator.Infrastructure.GitCli";
        var missingGitCli = Failures(SanctionedProcessStartRule(
            ProcessStartAllowlist.Select(entry => entry.FullName).Where(name => name != gitCli)));
        AssertNames(missingGitCli, gitCli);
        AssertReportedCallers(missingGitCli, [gitCli]);

        var allCallers = Failures(SanctionedProcessStartRule([]));
        AssertReportedCallers(allCallers, ProcessStartAllowlist.Select(entry => entry.FullName));
    }

    [Fact]
    public void Runtime_types_have_no_dashboard_namespace() => AssertPasses(NamespaceAbsenceRule(
        ["Mcg.AgentOrchestrator.App.Dashboard", "Mcg.AgentOrchestrator.Dashboard"]));

    [Fact]
    public void Namespace_control_detects_Cli_types() =>
        AssertNames(Failures(NamespaceAbsenceRule(["Mcg.AgentOrchestrator.App.Cli"])),
            "Mcg.AgentOrchestrator.App.Cli.");

    private static ICanBeEvaluated LayeringRule(ReflectionAssembly[] subjects, ReflectionAssembly[] forbidden) =>
        Types().That().ResideInAssembly(subjects[0], subjects.Skip(1).ToArray()).Should()
            .NotDependOnAny(Types().That().ResideInAssembly(forbidden[0], forbidden.Skip(1).ToArray()));

    private static ICanBeEvaluated RuntimeIsolationRule(ReflectionAssembly[] subjects) =>
        Types().That().ResideInAssembly(subjects[0], subjects.Skip(1).ToArray()).Should()
            .FollowCustomCondition(type =>
            {
                var forbidden = type.Dependencies.Select(dependency => dependency.Target.FullName)
                    .Where(name => name is "System.Diagnostics.Process" or "System.Diagnostics.ProcessStartInfo"
                        || IsInNamespace(name, "System.Net") || IsInNamespace(name, "Microsoft.Data.Sqlite"))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                return new ConditionResult(type, forbidden.Length == 0,
                    $"{type.FullName} depends on forbidden runtime types: {string.Join(", ", forbidden)}");
            }, "be isolated from process, network and SQLite runtime types");

    private static ICanBeEvaluated SanctionedProcessStartRule(IEnumerable<string> allowlist)
    {
        var sanctioned = allowlist.ToHashSet(StringComparer.Ordinal);
        return Types().That().ResideInAssembly(RuntimeAssemblies[0], RuntimeAssemblies.Skip(1).ToArray()).Should()
            .FollowCustomCondition(type =>
            {
                var callsStart = type.Dependencies.OfType<MethodCallDependency>().Any(dependency =>
                    dependency.TargetMember.DeclaringType.FullName == "System.Diagnostics.Process"
                    && dependency.TargetMember.Name.StartsWith("Start(", StringComparison.Ordinal));
                return new ConditionResult(type, !callsStart || sanctioned.Contains(OutermostTypeName(type)),
                    $"{type.FullName} ({OutermostTypeName(type)}) calls System.Diagnostics.Process.Start without sanction");
            }, "call Process.Start only from sanctioned declaring types or their nested types");
    }

    private static ICanBeEvaluated NamespaceAbsenceRule(string[] forbiddenNamespaces) =>
        Types().That().ResideInAssembly(RuntimeAssemblies[0], RuntimeAssemblies.Skip(1).ToArray()).Should()
            .FollowCustomCondition(type => new ConditionResult(type,
                !forbiddenNamespaces.Any(name => IsInNamespace(type.Namespace.FullName, name)),
                $"{type.FullName} resides in forbidden namespace {type.Namespace.FullName}"),
                "reside outside forbidden namespaces and their descendants");

    private static bool IsInNamespace(string name, string forbidden) =>
        name.Equals(forbidden, StringComparison.Ordinal) || name.StartsWith(forbidden + ".", StringComparison.Ordinal);

    private static string OutermostTypeName(IType type) => type.FullName.Split('+', '/')[0];

    private static EvaluationResult[] Failures(ICanBeEvaluated rule) =>
        rule.Evaluate(SharedArchitecture.Value).Where(result => !result.Passed).ToArray();

    private static void AssertPasses(ICanBeEvaluated rule)
    {
        var failures = Failures(rule);
        Assert.True(failures.Length == 0, failures.ToErrorMessage());
    }

    private static void AssertNames(EvaluationResult[] failures, string expected) =>
        Assert.True(failures.Any(result => result.Description.Contains(expected, StringComparison.Ordinal)),
            $"Expected a failed description naming {expected}. {failures.ToErrorMessage()}");

    private static void AssertNamesExactType(EvaluationResult[] failures, string expected) =>
        Assert.True(failures.Any(result => Regex.IsMatch(result.Description,
                $@"(?<![\w.]){Regex.Escape(expected)}(?![\w.])", RegexOptions.CultureInvariant)),
            $"Expected a failed description naming the exact type {expected}. {failures.ToErrorMessage()}");

    private static void AssertNamesSubject(EvaluationResult[] failures, ReflectionAssembly subject) =>
        Assert.True(failures.Any(result => result.EvaluatedObject is IType type
            && type.Assembly.FullName == subject.FullName
            && result.Description.Contains(type.FullName, StringComparison.Ordinal)), failures.ToErrorMessage());

    private static void AssertReportedCallers(EvaluationResult[] failures, IEnumerable<string> expected)
    {
        Assert.True(failures.All(result => result.EvaluatedObject is IType), failures.ToErrorMessage());
        var reported = failures.Select(result => OutermostTypeName((IType)result.EvaluatedObject))
            .ToHashSet(StringComparer.Ordinal);
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        Assert.True(reported.SetEquals(expectedSet),
            $"Expected callers: {string.Join(", ", expectedSet.Order(StringComparer.Ordinal))}. "
            + $"Reported callers: {string.Join(", ", reported.Order(StringComparer.Ordinal))}. {failures.ToErrorMessage()}");
    }
}

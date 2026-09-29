using System.Text.RegularExpressions;

public sealed class TestProcessStopIdentityGuardTests
{
    // Existing methods outside this goal. Remove entries as they are migrated; do not add entries.
    private static readonly HashSet<string> ExistingExceptions = new(StringComparer.Ordinal)
    {
        "DotnetBuildEnvironmentManagerTests.cs::StopFocusedProcessIdentity",
        "DispatchProcessHostTests.cs::DispatchProcessHostHeartbeatCpuAndPidsReflectWrappedGrandchild",
        "DispatchHostLifetimeHandoffTests.cs::KillExpectedHostAsync",
        "GoalAcceptanceVerifierDotnetBuildSlotTestsSlotGateJobResources.cs::ProductionRunnerCancellationKillsReadyDescendantTree",
        "GoalWorktreeTests.cs::CreateReuseFixture",
        "MtpTestRunnerScriptTests.cs::Run",
        "HermesAcpTrialTestsOwnedExit.cs::RootExitWaitRemainsCancellableUntilOwnedChildExits",
        "HermesAcpTrialTests.cs::ProcessLauncherInventoriesAndKillsOwnedChild",
        "GoalWorktreeTestsSqliteTooling.cs::KillOwnedProcessForTestCleanup",
        "ProcessTreeGuiSuppressionTestsBelowNormalInheritance.cs::CheckInheritance",
        "RealWorkerProcessGuardTests.cs::TryKillDispatchHost",
        "WorkerProcessJobsTests.cs::WorkerProcessJobsNonWindowsRegistrationFailureDisposesStartedProcess",
        "WorkerDispatchTestsWorkerResultClassification.cs::BackgroundDispatchRunnerRegistrationFailureStopsBeforeProcessReceipt",
        "Fixtures/ConsoleIoProbe/BelowNormalGrandchildProbe.cs::Run",
        "Fixtures/ConsoleIoProbe/StartupPipeProbe.cs::Launch"
    };

    private const int MaximumExistingExceptions = 15;
    private static readonly Regex MemberStart = new(
        @"(?m)^\s*(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>,?\[\].]+\s+(?<name>\w+)\s*\(",
        RegexOptions.Compiled);

    [Xunit.Fact]
    public void TestProcessStopsRequireRecordedIdentity()
    {
        var offenders = FindOffenders();
        Xunit.Assert.True(ExistingExceptions.Count <= MaximumExistingExceptions);
        Xunit.Assert.True(ExistingExceptions.All(offenders.Contains),
            "Remove resolved allow-list entries: " + string.Join(", ", ExistingExceptions.Except(offenders)));
        Xunit.Assert.True(offenders.All(ExistingExceptions.Contains),
            "Test process stops must use a recorded identity. Offenders: " +
            string.Join(", ", offenders.Except(ExistingExceptions)));
    }

    private static HashSet<string> FindOffenders([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        var root = VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot)
            ? Path.Combine(verifiedRoot, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests")
            : Path.GetDirectoryName(thisFile)!;
        var offenders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                relative is "TestOwnedProcessStop.cs" or "TestProcessStopIdentityGuardTests.cs")
                continue;

            var source = File.ReadAllText(file);
            var members = MemberStart.Matches(source);
            for (var index = 0; index < members.Count; index++)
            {
                var start = members[index].Index;
                var end = index + 1 < members.Count ? members[index + 1].Index : source.Length;
                var body = source[start..end];
                var bareReopenStop = body.Contains("GetProcessById", StringComparison.Ordinal) &&
                    body.Contains(".Kill(", StringComparison.Ordinal);
                var bareSuccessorStop = Regex.IsMatch(body,
                    @"ConductorLoopHandoff\.StopFailedSuccessor\(\s*[A-Za-z_]\w*\s*\)") &&
                    !body.Contains("SuccessorIdentity", StringComparison.Ordinal) &&
                    !body.Contains("ConductorSupervisorProcessIdentity", StringComparison.Ordinal);
                var taskkill = body.Contains("taskkill", StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(body, @"(?:ArgumentList|Arguments|StartInfo).*?/PID", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (bareReopenStop || bareSuccessorStop || taskkill)
                    offenders.Add(relative + "::" + members[index].Groups["name"].Value);
            }
        }
        return offenders;
    }
}

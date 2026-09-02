using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsFocusedEvidence : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public async Task FocusedEvidence_SixTargetsOneProject_PreservesOneFilteredCheckAndEveryTarget()
    {
        string[] targets =
        [
            "CliCommandTests",
            "WorkerShellTests",
            "WorkerSandboxCapabilityPlannerTests",
            "DashboardValidationHarnessTests",
            "ConductorDriverTests",
            "GoalWorktreeTests"
        ];
        var (result, calls) = await RunMappedEvidenceAsync(
            $"Infrastructure.Tests: {string.Join(',', targets)}");

        Assert.True(result.Accepted);
        Assert.True(result.Passed);
        var check = Assert.Single(result.Checks);
        Assert.StartsWith("reviewer focused evidence: Infrastructure.Tests ", check.Name, StringComparison.Ordinal);
        Assert.Contains("mode=focused reason=explicit-focused-mapping", result.Summary);
        Assert.NotNull(result.Coverage);
        Assert.Equal("focused", result.Coverage.ExecutionMode);
        Assert.Equal("explicit-focused-mapping", result.Coverage.ExecutionReason);
        var targetCoverage = Assert.Single(result.Coverage.TargetToChecks);
        Assert.Equal(
            $"Infrastructure.Tests: {string.Join(',', targets)}",
            targetCoverage.Target);
        Assert.Equal(check.Name, Assert.Single(targetCoverage.CheckNames));
        var testCall = Assert.Single(calls.Where(call =>
            IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests")));
        Assert.Contains("--filter-class", testCall);
        Assert.All(targets, target =>
            Assert.Contains(testCall, argument => argument.Contains(target, StringComparison.Ordinal)));
    }

    [Xunit.Theory]
    [Xunit.InlineData("GoalAcceptanceVerifierTests")]
    [Xunit.InlineData("Infrastructure.Tests: GoalAcceptanceVerifierTests")]
    [Xunit.InlineData("FullyQualifiedName~GoalAcceptanceVerifierTests")]
    [Xunit.InlineData("Infrastructure.Tests: FullyQualifiedName~GoalAcceptanceVerifierTests")]
    public async Task FocusedEvidenceAcceptsBareAndFullyQualifiedClassSpellings(string request)
    {
        var (result, calls) = await RunMappedEvidenceAsync(request);

        Assert.True(result.Accepted);
        Assert.True(result.Passed);
        var testCall = Assert.Single(calls.Where(call =>
            IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests")));
        AssertArgumentPair(testCall, "--filter-class", "*GoalAcceptanceVerifierTests*");
        Assert.DoesNotContain("Infrastructure.Tests:", testCall);
    }

    [Xunit.Fact]
    public async Task MethodQualifiedFocusedEvidenceUsesExactMethodSelector()
    {
        var (result, calls) = await RunMappedEvidenceAsync(
            "Infrastructure.Tests: FullyQualifiedName~SelectionProbeTests.SelectsOneMethod",
            configureWorkspace: root =>
            {
                var directory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
                Directory.CreateDirectory(directory);
                File.WriteAllText(
                    Path.Combine(directory, "SelectionProbeTests.cs"),
                    """
                    sealed class SelectionProbeTests
                    {
                        [Xunit.Fact]
                        public void SelectsOneMethod() { }
                    }
                    """);
            });

        Assert.True(result.Accepted);
        Assert.True(result.Passed);
        var testCall = Assert.Single(calls.Where(call =>
            IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests")));
        AssertArgumentPair(
            testCall,
            "--filter-method",
            "*SelectionProbeTests.SelectsOneMethod*");
        Assert.DoesNotContain("--filter-class", testCall);
    }

    [Xunit.Fact]
    public async Task MethodQualifiedFocusedEvidencePreservesContainsSemantics()
    {
        var (result, calls) = await RunMappedEvidenceAsync(
            "Infrastructure.Tests: FullyQualifiedName~SelectionProbeTests.SelectsOne",
            configureWorkspace: root =>
            {
                var directory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
                Directory.CreateDirectory(directory);
                File.WriteAllText(
                    Path.Combine(directory, "SelectionProbeTests.cs"),
                    """
                    sealed class SelectionProbeTests
                    {
                        [Xunit.Fact]
                        public void SelectsOneMethod() { }
                    }
                    """);
            });

        Assert.True(result.Accepted);
        var testCall = Assert.Single(calls.Where(call =>
            IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests")));
        AssertArgumentPair(testCall, "--filter-method", "*SelectionProbeTests.SelectsOne*");
        Assert.DoesNotContain("--filter-class", testCall);
    }

    [Xunit.Fact]
    public async Task DeclarationLookingTextInsideRawStringIsNotResolvedAsTestMethod()
    {
        const string offendingToken = "FullyQualifiedName~SelectionProbeTests.SelectsOneMethod";
        var (result, calls) = await RunMappedEvidenceAsync(
            $"Infrastructure.Tests: {offendingToken}",
            configureWorkspace: root =>
            {
                var directory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
                Directory.CreateDirectory(directory);
                File.WriteAllText(
                    Path.Combine(directory, "SelectionProbeTests.cs"),
                    """"
                    sealed class SelectionProbeTests
                    {
                        private const string Fixture = """
                            [Xunit.Fact]
                            public void SelectsOneMethod() { }
                            """;
                    }
                    """");
            });

        Assert.False(result.Accepted);
        Assert.Equal(FocusedEvidenceRejectionCode.UnresolvableSelection, result.Rejection?.Code);
        Assert.Equal(" " + offendingToken, result.Rejection?.OffendingToken);
        Assert.Empty(calls);
    }

    [Xunit.Fact]
    public async Task UnreadableFocusedEvidenceSourceIsTypedAsApparatusFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string offendingToken = "FullyQualifiedName~LockedSelectionProbeTests.SelectsOneMethod";
        var (result, calls) = await RunMappedEvidenceAsync(
            $"Infrastructure.Tests: {offendingToken}",
            configureWorkspace: root =>
            {
                var directory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
                Directory.CreateDirectory(directory);
                File.WriteAllText(
                    Path.Combine(directory, "LockedSelectionProbeTests.cs"),
                    """
                    sealed class LockedSelectionProbeTests
                    {
                        [Xunit.Fact]
                        public void SelectsOneMethod() { }
                    }
                    """);
            },
            holdWorkspaceResource: root => File.Open(
                Path.Combine(
                    root,
                    "tests",
                    "Mcg.AgentOrchestrator.Infrastructure.Tests",
                    "LockedSelectionProbeTests.cs"),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None));

        Assert.False(result.Accepted);
        Assert.Equal(FocusedEvidenceRejectionCode.SourceDiscoveryFailure, result.Rejection?.Code);
        Assert.Equal(" " + offendingToken, result.Rejection?.OffendingToken);
        Assert.Empty(calls);
    }

    [Xunit.Fact]
    public async Task InvocationNameIsNotResolvedAsTestMethod()
    {
        const string offendingToken = "FullyQualifiedName~GoalAcceptanceVerifierTests.WriteAllText";
        var (result, calls) = await RunMappedEvidenceAsync($"Infrastructure.Tests: {offendingToken}");

        Assert.False(result.Accepted);
        Assert.Equal(FocusedEvidenceRejectionCode.UnresolvableSelection, result.Rejection?.Code);
        Assert.Equal(" " + offendingToken, result.Rejection?.OffendingToken);
        Assert.Empty(calls);
    }

    [Xunit.Fact]
    public async Task SiblingTestMethodIsNotResolvedForSelectedClass()
    {
        const string offendingToken = "FullyQualifiedName~SelectionProbeTests.SiblingMethod";
        var (result, calls) = await RunMappedEvidenceAsync(
            $"Infrastructure.Tests: {offendingToken}",
            configureWorkspace: root =>
            {
                var directory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
                Directory.CreateDirectory(directory);
                File.WriteAllText(
                    Path.Combine(directory, "SelectionProbeTests.cs"),
                    """
                    sealed class SelectionProbeTests { }

                    sealed class SiblingTests
                    {
                        [Xunit.Fact]
                        public void SiblingMethod() { }
                    }
                    """);
            });

        Assert.False(result.Accepted);
        Assert.Equal(FocusedEvidenceRejectionCode.UnresolvableSelection, result.Rejection?.Code);
        Assert.Equal(" " + offendingToken, result.Rejection?.OffendingToken);
        Assert.Empty(calls);
    }

    [Xunit.Fact]
    public async Task UnsupportedFocusedEvidenceTokenIsTypedAndExactBeforeLaunch()
    {
        const string offendingToken = "Bogus == token";
        var (result, calls) = await RunMappedEvidenceAsync(
            $"Infrastructure.Tests: FullyQualifiedName~GoalAcceptanceVerifierTests|{offendingToken}");

        Assert.False(result.Accepted);
        Assert.False(result.Passed);
        Assert.NotNull(result.Rejection);
        Assert.Equal(FocusedEvidenceRejectionCode.UnsupportedToken, result.Rejection.Code);
        Assert.Equal(offendingToken, result.Rejection.OffendingToken);
        Assert.Contains(offendingToken, result.Rejection.Detail, StringComparison.Ordinal);
        Assert.Empty(calls);
    }

    [Xunit.Fact]
    public async Task BareInvalidTokenPreservesOriginalWhitespace()
    {
        const string offendingToken = " Bogus == token ";
        var (result, calls) = await RunMappedEvidenceAsync(
            $"Infrastructure.Tests: GoalAcceptanceVerifierTests,{offendingToken}");

        Assert.False(result.Accepted);
        Assert.Equal(FocusedEvidenceRejectionCode.UnsupportedToken, result.Rejection?.Code);
        Assert.Equal(offendingToken, result.Rejection?.OffendingToken);
        Assert.Empty(calls);
    }

    [Xunit.Fact]
    public async Task UnresolvableMethodSelectionIsRejectedWithoutClassFallback()
    {
        const string offendingToken = "FullyQualifiedName~MissingTests.MissingMethod";
        var (result, calls) = await RunMappedEvidenceAsync(
            $"Infrastructure.Tests: {offendingToken}");

        Assert.False(result.Accepted);
        Assert.Equal(FocusedEvidenceRejectionCode.UnresolvableSelection, result.Rejection?.Code);
        Assert.Equal(" " + offendingToken, result.Rejection?.OffendingToken);
        Assert.Empty(calls);
    }

    [Xunit.Fact]
    public async Task OversizedFocusedEvidenceTokenIsRejectedBeforeLaunch()
    {
        var offendingToken = "FullyQualifiedName~" + new string('A', 1025);
        var (result, calls) = await RunMappedEvidenceAsync($"Infrastructure.Tests: {offendingToken}");

        Assert.False(result.Accepted);
        Assert.Equal(FocusedEvidenceRejectionCode.OversizedFilter, result.Rejection?.Code);
        Assert.Equal(" " + offendingToken, result.Rejection?.OffendingToken);
        Assert.Empty(calls);
    }

    [Xunit.Fact]
    public async Task WhitespacePaddedTokenCannotBypassLengthLimit()
    {
        var offendingToken = "GoalAcceptanceVerifierTests" + new string(' ', 1024);
        var (result, calls) = await RunMappedEvidenceAsync($"Infrastructure.Tests: {offendingToken}");

        Assert.False(result.Accepted);
        Assert.Equal(FocusedEvidenceRejectionCode.OversizedFilter, result.Rejection?.Code);
        Assert.Equal(" " + offendingToken, result.Rejection?.OffendingToken);
        Assert.Empty(calls);
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(8)]
    public async Task FocusedEvidenceZeroExecutedTestsIsApparatusFailure(int exitCode)
    {
        var (result, _) = await RunMappedEvidenceAsync(
            "Infrastructure.Tests: GoalAcceptanceVerifierTests",
            executedTestCount: 0,
            testExitCode: exitCode);

        Assert.True(result.Accepted);
        Assert.False(result.Passed);
        Assert.Equal(FindingEvidenceOutcomeReason.ApparatusFailure, result.OutcomeReason);
        var arm = Assert.Single(result.Arms!);
        Assert.Equal(FindingEvidenceArmDisposition.ApparatusFailure, arm.Disposition);
        var check = Assert.Single(result.Checks);
        Assert.Equal(0, check.ExecutedTestCount);
        Assert.Equal(
            AcceptanceFailureClassifications.FocusedSelectionApparatusFailure,
            check.FailureClassification);
        Assert.Empty(check.FailingTestIdentities!);
        Assert.Contains("executed 0 tests", check.OutputTail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void FocusedEvidence_BaselineApparatusFailureOverridesCandidateRed()
    {
        var outcome = GoalAcceptanceVerifier.ClassifyFocusedEvidenceExperiment(
            CreateFocusedEvidenceArm(FindingEvidenceArm.Candidate, FindingEvidenceArmDisposition.Red),
            CreateFocusedEvidenceArm(FindingEvidenceArm.Baseline, FindingEvidenceArmDisposition.ApparatusFailure));

        Assert.Equal(FindingEvidenceOutcomeReason.ApparatusFailure, outcome);
    }

    [Xunit.Fact]
    public void FocusedEvidence_BaselineApparatusFailureOverridesCandidateInconclusive()
    {
        var outcome = GoalAcceptanceVerifier.ClassifyFocusedEvidenceExperiment(
            CreateFocusedEvidenceArm(FindingEvidenceArm.Candidate, FindingEvidenceArmDisposition.Inconclusive),
            CreateFocusedEvidenceArm(FindingEvidenceArm.Baseline, FindingEvidenceArmDisposition.ApparatusFailure));

        Assert.Equal(FindingEvidenceOutcomeReason.ApparatusFailure, outcome);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_new_test_absent_at_baseline_does_not_poison_attribution")]
    public void NewTestAbsentAtBaselineDoesNotPoisonAttribution()
    {
        const string introduced = "Sample.Tests.NewFailure";
        const string inherited = "Sample.Tests.ExistingFailure";
        const string ambiguous = "Sample.Tests.AmbiguousZeroMatch";
        const string introducedCheck = "focused new failure";
        const string inheritedCheck = "focused existing failure";
        const string ambiguousCheck = "focused ambiguous failure";
        var baseline = new FocusedEvidenceArmRunResult(
            FindingEvidenceArm.Baseline,
            "main-a",
            FindingEvidenceArmDisposition.ApparatusFailure,
            Accepted: true,
            Passed: false,
            "one selector is absent and one is red",
            [
                new AcceptanceCheckResult(
                    introducedCheck,
                    false,
                    null,
                    "absent from baseline source",
                    FailureClassification: AcceptanceFailureClassifications.FocusedSelectionAbsentAtBaseline,
                    ExecutedTestCount: 0),
                new AcceptanceCheckResult(
                    inheritedCheck,
                    false,
                    1,
                    "existing failure",
                    FailingTestIdentities: [inherited],
                    ExecutedTestCount: 1),
                new AcceptanceCheckResult(
                    ambiguousCheck,
                    false,
                    8,
                    "executed 0 tests",
                    FailureClassification: AcceptanceFailureClassifications.FocusedSelectionApparatusFailure,
                    ExecutedTestCount: 0)
            ]);

        var attributions = AcceptanceFailureAttributionPlanner.ClassifyBaselineFailures(
            [introduced, inherited, ambiguous],
            [introduced, inherited, ambiguous],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [introduced] = introducedCheck,
                [inherited] = inheritedCheck,
                [ambiguous] = ambiguousCheck
            },
            baseline);

        Assert.Equal(AcceptanceTestFailureOrigin.Introduced, attributions[0].Origin);
        Assert.Contains("absent at merge-base", attributions[0].Evidence, StringComparison.Ordinal);
        Assert.Equal(AcceptanceTestFailureOrigin.Inherited, attributions[1].Origin);
        Assert.Contains("same focused identity failed", attributions[1].Evidence, StringComparison.Ordinal);
        Assert.Equal(AcceptanceTestFailureOrigin.Unattributed, attributions[2].Origin);
        Assert.Contains("apparatus was unavailable", attributions[2].Evidence, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void DifferentTheoryDataCaseAtBaselineRemainsIntroduced()
    {
        const string candidateIdentity = "Sample.Tests.TheoryTests.Fails(value: 42)";
        const string baselineIdentity = "Sample.Tests.TheoryTests.Fails(value: 1)";
        const string selector = "Sample.Tests.TheoryTests.Fails";
        const string checkName = "focused baseline theory method";
        var baseline = new FocusedEvidenceArmRunResult(
            FindingEvidenceArm.Baseline,
            "main-a",
            FindingEvidenceArmDisposition.Red,
            Accepted: true,
            Passed: false,
            "different theory case failed",
            [
                new AcceptanceCheckResult(
                    checkName,
                    Passed: false,
                    ExitCode: 1,
                    OutputTail: "baseline data-case failure",
                    FailingTestIdentities: [baselineIdentity],
                    ExecutedTestCount: 1)
            ]);

        var attribution = Assert.Single(AcceptanceFailureAttributionPlanner.ClassifyBaselineFailures(
            [candidateIdentity],
            [selector],
            new Dictionary<string, string>(StringComparer.Ordinal) { [selector] = checkName },
            baseline));

        Assert.Equal(AcceptanceTestFailureOrigin.Introduced, attribution.Origin);
        Assert.Equal(candidateIdentity, attribution.TestIdentity);
        Assert.Contains("data-case identity", attribution.Evidence, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MethodOnlyTheoryIdentityAtBaselineRemainsUnattributed()
    {
        const string candidateIdentity = "Sample.Tests.TheoryTests.Fails(value: 42)";
        const string selector = "Sample.Tests.TheoryTests.Fails";
        const string checkName = "focused baseline theory method";
        var baseline = new FocusedEvidenceArmRunResult(
            FindingEvidenceArm.Baseline,
            "main-a",
            FindingEvidenceArmDisposition.Red,
            Accepted: true,
            Passed: false,
            "baseline reporter omitted the data case",
            [
                new AcceptanceCheckResult(
                    checkName,
                    Passed: false,
                    ExitCode: 1,
                    OutputTail: "method-only baseline failure",
                    FailingTestIdentities: [selector],
                    ExecutedTestCount: 1)
            ]);

        var attribution = Assert.Single(AcceptanceFailureAttributionPlanner.ClassifyBaselineFailures(
            [candidateIdentity],
            [selector],
            new Dictionary<string, string>(StringComparer.Ordinal) { [selector] = checkName },
            baseline));

        Assert.Equal(AcceptanceTestFailureOrigin.Unattributed, attribution.Origin);
        Assert.Contains("exact data-case identity was unavailable", attribution.Evidence, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task FailureAttributionCapsRunsAndKeepsOmittedUnattributed()
    {
        const string project =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var projectDirectory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(
            Path.Combine(projectDirectory, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
            "<Project />");
        var identities = Enumerable
            .Range(0, GoalAcceptanceVerifier.MaxFailureAttributionFocusedEvidenceIdentities + 2)
            .Select(index => $"Sample.Tests.AttributionCapTests.Failure{index:D2}")
            .ToArray();
        File.WriteAllText(
            Path.Combine(projectDirectory, "AttributionCapTests.cs"),
            "namespace Sample.Tests;\n\npublic sealed class AttributionCapTests\n{\n" +
            string.Join("\n", identities.Select(identity =>
                $"    [Xunit.Fact]\n    public void {identity[(identity.LastIndexOf('.') + 1)..]}() {{ }}")) +
            "\n}\n");
        var testCalls = new System.Collections.Concurrent.ConcurrentBag<string[]>();
        try
        {
            AssertGitSucceeded(root, "init", "-b", "main");
            AssertGitSucceeded(root, "config", "user.email", "attribution-cap@example.invalid");
            AssertGitSucceeded(root, "config", "user.name", "Attribution Cap Fixture");
            AssertGitSucceeded(root, "add", ".");
            AssertGitSucceeded(root, "commit", "-m", "baseline");
            var verifier = new GoalAcceptanceVerifier((args, _, _, _) =>
            {
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    testCalls.Add(args);
                    var methodIndex = Array.IndexOf(args, "--filter-method");
                    Assert.True(methodIndex >= 0 && methodIndex + 1 < args.Length);
                    var selectedIdentity = args[methodIndex + 1].Trim('*');
                    WriteMtpTrx(args, executedTestCount: 1, [selectedIdentity]);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0,
                        "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });
            var check = new AcceptanceCheckResult(
                "core tests",
                Passed: false,
                ExitCode: 1,
                OutputTail: "candidate failures",
                FailingTestIdentities: identities,
                TestProjectPath: project);
            var attributed = await verifier.AttachTestFailureAttributionsAsync(
                check,
                new GoalAcceptanceVerifier.AcceptanceManifestCheck
                {
                    Name = check.Name,
                    Type = "dotnet-test",
                    Runner = "mtp",
                    Project = project
                },
                AcceptanceGateEngineSettings.Load(root),
                root,
                GoalId.New(),
                stableSlotIndex: null,
                stableSlotLease: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(
                GoalAcceptanceVerifier.MaxFailureAttributionFocusedEvidenceIdentities,
                testCalls.Count);
            Assert.Equal(identities.Length, attributed.FailingTestAttributions!.Count);
            var omitted = attributed.FailingTestAttributions
                .Where(attribution => attribution.Evidence.Contains("omitted", StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(2, omitted.Length);
            Assert.All(omitted, attribution =>
                Assert.Equal(AcceptanceTestFailureOrigin.Unattributed, attribution.Origin));
            Assert.Equal(
                identities.Skip(GoalAcceptanceVerifier.MaxFailureAttributionFocusedEvidenceIdentities),
                omitted.Select(attribution => attribution.TestIdentity));
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task FailureAttributionCapsRunsAcrossAllFailedChecks()
    {
        const string project =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
        var root = CreateManifestWorkspace($$"""
            {
              "version": 1,
              "checks": [
                { "name": "infrastructure tests: First", "type": "dotnet-test", "runner": "mtp", "project": "{{project}}", "arguments": ["--filter", "FullyQualifiedName~FirstAttributionTests"] },
                { "name": "infrastructure tests: Second", "type": "dotnet-test", "runner": "mtp", "project": "{{project}}", "arguments": ["--filter", "FullyQualifiedName~SecondAttributionTests"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var firstIdentities = Enumerable.Range(0, 3)
            .Select(index => $"Sample.Tests.FirstAttributionTests.Failure{index:D2}")
            .ToArray();
        var secondIdentities = Enumerable.Range(0, 3)
            .Select(index => $"Sample.Tests.SecondAttributionTests.Failure{index:D2}")
            .ToArray();
        var projectDirectory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(
            Path.Combine(projectDirectory, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
            "<Project />");
        File.WriteAllText(
            Path.Combine(projectDirectory, "AttributionTests.cs"),
            "namespace Sample.Tests;\n\n" +
            RenderClass("FirstAttributionTests", firstIdentities) + "\n" +
            RenderClass("SecondAttributionTests", secondIdentities));
        var focusedCalls = new System.Collections.Concurrent.ConcurrentBag<string[]>();
        var focusedWorkingDirectories = new System.Collections.Concurrent.ConcurrentBag<string>();
        var focusedBuildCalls = new System.Collections.Concurrent.ConcurrentBag<string[]>();
        try
        {
            AssertGitSucceeded(root, "init", "-b", "main");
            AssertGitSucceeded(root, "config", "user.email", "gate-cap@example.invalid");
            AssertGitSucceeded(root, "config", "user.name", "Gate Cap Fixture");
            AssertGitSucceeded(root, "add", ".");
            AssertGitSucceeded(root, "commit", "-m", "baseline");
            var verifier = new GoalAcceptanceVerifier((args, workingDirectory, _, _) =>
            {
                if (args is ["dotnet", "build", ..] &&
                    workingDirectory.Contains("mcg-focused-evidence-baselines", StringComparison.OrdinalIgnoreCase))
                {
                    focusedBuildCalls.Add(args);
                }

                if (!IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
                }

                var methodIndex = Array.IndexOf(args, "--filter-method");
                if (methodIndex >= 0)
                {
                    focusedCalls.Add(args);
                    focusedWorkingDirectories.Add(workingDirectory);
                    var selectedIdentity = args[methodIndex + 1].Trim('*');
                    WriteFailedMtpTrx(args, [selectedIdentity]);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        1,
                        "Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1."));
                }

                var filterIndex = Array.IndexOf(args, "--filter-class");
                var identities = filterIndex >= 0 &&
                    args[filterIndex + 1].Contains("FirstAttributionTests", StringComparison.Ordinal)
                    ? firstIdentities
                    : secondIdentities;
                WriteFailedMtpTrx(args, identities);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    1,
                    $"Failed! - Failed: {identities.Length}, Passed: 0, Skipped: 0, Total: {identities.Length}."));
            });

            var result = await verifier.RunAsync(root, GoalId.New(), stableSlotIndex: null);

            Assert.False(result.Passed);
            Assert.Equal(2, result.Checks!.Count(check => !check.Passed));
            Assert.Equal(
                GoalAcceptanceVerifier.MaxFailureAttributionFocusedEvidenceIdentities,
                focusedCalls.Count);
            Assert.Single(focusedWorkingDirectories.Distinct(StringComparer.OrdinalIgnoreCase));
            Assert.Single(focusedBuildCalls);
            var attributions = result.Checks!
                .Where(check => !check.Passed)
                .SelectMany(check => check.FailingTestAttributions ?? [])
                .ToArray();
            Assert.Equal(firstIdentities.Length + secondIdentities.Length, attributions.Length);
            var omitted = attributions.Where(attribution =>
                attribution.Evidence.Contains("omitted", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, omitted.Length);
            Assert.All(omitted, attribution =>
                Assert.Equal(AcceptanceTestFailureOrigin.Unattributed, attribution.Origin));
            Assert.All(
                attributions.Except(omitted),
                attribution => Assert.Equal(AcceptanceTestFailureOrigin.Inherited, attribution.Origin));
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }

        static string RenderClass(string className, IEnumerable<string> identities) =>
            $"public sealed class {className}\n{{\n" +
            string.Join("\n", identities.Select(identity =>
                $"    [Xunit.Fact] public void {identity[(identity.LastIndexOf('.') + 1)..]}() {{ }}")) +
            "\n}\n";

        static void WriteFailedMtpTrx(string[] args, IReadOnlyList<string> identities)
        {
            var resultsDirectoryIndex = Array.IndexOf(args, "--results-directory");
            var trxFileIndex = Array.IndexOf(args, "--report-trx-filename");
            Assert.True(resultsDirectoryIndex >= 0 && resultsDirectoryIndex + 1 < args.Length);
            Assert.True(trxFileIndex >= 0 && trxFileIndex + 1 < args.Length);
            Directory.CreateDirectory(args[resultsDirectoryIndex + 1]);
            var destinationPath = Path.Combine(args[resultsDirectoryIndex + 1], args[trxFileIndex + 1]);
            var definitions = identities.Select((identity, index) =>
            {
                var separator = identity.LastIndexOf('.');
                return new System.Xml.Linq.XElement(
                    "UnitTest",
                    new System.Xml.Linq.XAttribute("id", $"failed-{index}"),
                    new System.Xml.Linq.XElement(
                        "TestMethod",
                        new System.Xml.Linq.XAttribute("className", identity[..separator]),
                        new System.Xml.Linq.XAttribute("name", identity[(separator + 1)..])));
            });
            var results = identities.Select((identity, index) =>
                new System.Xml.Linq.XElement(
                    "UnitTestResult",
                    new System.Xml.Linq.XAttribute("testId", $"failed-{index}"),
                    new System.Xml.Linq.XAttribute("testName", identity),
                    new System.Xml.Linq.XAttribute("outcome", "Failed")));
            new System.Xml.Linq.XDocument(
                new System.Xml.Linq.XElement(
                    "TestRun",
                    new System.Xml.Linq.XElement("TestDefinitions", definitions),
                    new System.Xml.Linq.XElement("Results", results),
                    new System.Xml.Linq.XElement(
                        "ResultSummary",
                        new System.Xml.Linq.XAttribute("outcome", "Failed"),
                        new System.Xml.Linq.XElement(
                            "Counters",
                            new System.Xml.Linq.XAttribute("total", identities.Count),
                            new System.Xml.Linq.XAttribute("executed", identities.Count),
                            new System.Xml.Linq.XAttribute("passed", 0),
                            new System.Xml.Linq.XAttribute("failed", identities.Count),
                            new System.Xml.Linq.XAttribute("notExecuted", 0)))))
                .Save(destinationPath);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_candidate_only_method_is_classified_absent_before_baseline_execution")]
    public void CandidateOnlyMethodIsClassifiedAbsentBeforeBaselineExecution()
    {
        const string project =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
        const string identity = "Sample.Tests.ExistingTests.CandidateOnlyFailure";
        var candidateRoot = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var baselineRoot = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            WriteSelectionProject(candidateRoot, includeCandidateOnlyMethod: true);
            WriteSelectionProject(baselineRoot, includeCandidateOnlyMethod: false);
            var settings = AcceptanceGateEngineSettings.Load(candidateRoot);
            var candidate = AcceptanceFailureAttributionPlanner.BuildCandidateSelections(
                "Infrastructure.Tests",
                [identity],
                settings,
                candidateRoot);

            Assert.True(candidate.Succeeded, candidate.FailureEvidence);
            var baseline = AcceptanceFailureAttributionPlanner.BuildBaselineSourceSelections(
                "merge-base-sha",
                candidate.Checks,
                GoalAcceptanceVerifier.ProjectLabel,
                settings,
                baselineRoot);

            Assert.Empty(baseline.ExecutableChecks);
            var absent = Assert.Single(baseline.SourceClassificationChecks);
            Assert.Equal(Assert.Single(candidate.Checks).Name, absent.Name);
            Assert.Equal(project, absent.TestProjectPath);
            Assert.Equal(
                AcceptanceFailureClassifications.FocusedSelectionAbsentAtBaseline,
                absent.FailureClassification);
            Assert.Equal(0, absent.ExecutedTestCount);
            Assert.Contains("does not exist in baseline source", absent.OutputTail, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryWithRetry(candidateRoot);
            DeleteDirectoryWithRetry(baselineRoot);
        }

        static void WriteSelectionProject(string root, bool includeCandidateOnlyMethod)
        {
            var directory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"), "<Project />");
            File.WriteAllText(
                Path.Combine(directory, "ExistingTests.cs"),
                includeCandidateOnlyMethod
                    ? "namespace Sample.Tests; sealed class ExistingTests { [Xunit.Fact] public void CandidateOnlyFailure() { } }"
                    : "namespace Sample.Tests; sealed class ExistingTests { [Xunit.Fact] public void ExistingPassingTest() { } }");
        }
    }

    [Xunit.Fact]
    public async Task FocusedEvidence_TwoReceiptTargetsAcrossProjects_PreserveFocusedChecks()
    {
        var (result, calls) = await RunMappedEvidenceAsync(
            "Core.Tests: DispatchOutcomeClassifyTests; " +
            "Infrastructure.Tests: WorkerDispatchTestsWorkerResultClassification");

        Assert.True(result.Accepted);
        Assert.True(result.Passed);
        Assert.Equal(
            [
                "reviewer focused evidence: Core.Tests FullyQualifiedName~DispatchOutcomeClassifyTests",
                "reviewer focused evidence: Infrastructure.Tests FullyQualifiedName~WorkerDispatchTestsWorkerResultClassification"
            ],
            result.Checks.Select(check => check.Name));
        Assert.Contains("mode=focused reason=explicit-focused-mapping", result.Summary);
        var testCalls = calls
            .Where(call =>
                IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Core.Tests") ||
                IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            .ToArray();
        Assert.Equal(2, testCalls.Length);
        Assert.True(IsMtpExecutableCall(testCalls[0], "Mcg.AgentOrchestrator.Core.Tests"));
        Assert.True(IsMtpExecutableCall(testCalls[1], "Mcg.AgentOrchestrator.Infrastructure.Tests"));
        Assert.All(testCalls, call => Assert.Contains("--filter-class", call));
    }

    [Xunit.Fact]
    public async Task FocusedEvidence_MultipleMappedItems_PreservesFocusedChecks()
    {
        // Unchanged-behaviour guard: this passes before and after the overflow fix.
        var (result, _) = await RunMappedEvidenceAsync(
            "Infrastructure.Tests: AlphaTests,BetaTests; Core.Tests: GammaTests,DeltaTests");

        Assert.True(result.Accepted);
        Assert.True(result.Passed);
        Assert.Equal(
            [
                "reviewer focused evidence: Infrastructure.Tests FullyQualifiedName~AlphaTests|FullyQualifiedName~BetaTests",
                "reviewer focused evidence: Core.Tests FullyQualifiedName~GammaTests|FullyQualifiedName~DeltaTests"
            ],
            result.Checks.Select(check => check.Name));
        Assert.Contains("mode=focused reason=explicit-focused-mapping", result.Summary);
    }

    [Xunit.Fact]
    public async Task FocusedEvidence_CompatibleSameProjectItemsUseOneDeterministicInvocation()
    {
        var (result, calls) = await RunMappedEvidenceAsync(
            "Infrastructure.Tests: GateReadyCandidateProjectorTests; " +
            "Infrastructure.Tests: ConductorDriverTests; " +
            "Infrastructure.Tests: GateReadyCandidateProjectorTests",
            executedTestCount: 2,
            executedTestIdentities:
            [
                "Mcg.AgentOrchestrator.Infrastructure.Tests.ConductorDriverTests.ExecutedMember",
                "Mcg.AgentOrchestrator.Infrastructure.Tests.GateReadyCandidateProjectorTests.ExecutedMember"
            ]);

        Assert.True(result.Accepted);
        Assert.True(result.Passed);
        var check = Assert.Single(result.Checks);
        Assert.Equal(
            "reviewer focused evidence: Infrastructure.Tests " +
            "FullyQualifiedName~ConductorDriverTests|FullyQualifiedName~GateReadyCandidateProjectorTests",
            check.Name);
        Assert.Contains("compatible-same-project-batch", result.Summary, StringComparison.Ordinal);
        var testCall = Assert.Single(calls.Where(call =>
            IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests")));
        Assert.Contains("--filter-class", testCall);
        Assert.Contains("*ConductorDriverTests*", testCall);
        Assert.Contains("*GateReadyCandidateProjectorTests*", testCall);
    }

    [Xunit.Fact]
    public async Task FocusedEvidence_CompatibleBatchReportsUncoveredSelectionAsApparatusFailure()
    {
        var (result, calls) = await RunMappedEvidenceAsync(
            "Infrastructure.Tests: ConductorDriverTests; " +
            "Infrastructure.Tests: GateReadyCandidateProjectorTests",
            executedTestCount: 1,
            executedTestIdentities:
            [
                "Mcg.AgentOrchestrator.Infrastructure.Tests.ConductorDriverTests.ExecutedMember"
            ]);

        Assert.True(result.Accepted);
        Assert.False(result.Passed);
        Assert.Equal(FindingEvidenceOutcomeReason.ApparatusFailure, result.OutcomeReason);
        var check = Assert.Single(result.Checks);
        Assert.Equal(1, check.ExecutedTestCount);
        Assert.Equal(
            AcceptanceFailureClassifications.FocusedSelectionApparatusFailure,
            check.FailureClassification);
        Assert.Contains("GateReadyCandidateProjectorTests", check.OutputTail, StringComparison.Ordinal);
        Assert.Contains("matching 0 tests", check.OutputTail, StringComparison.Ordinal);
        Assert.Single(calls.Where(call =>
            IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests")));
    }

    [Xunit.Fact]
    public async Task FocusedEvidence_MethodPrefixCoverageUsesRunnerSubstringSemantics()
    {
        var (result, _) = await RunMappedEvidenceAsync(
            "Infrastructure.Tests: GoalAcceptanceVerifierTests.FocusedEvidence_CompatibleBatchReports; " +
            "Infrastructure.Tests: ConductorDriverTests",
            executedTestCount: 2,
            executedTestIdentities:
            [
                "Mcg.AgentOrchestrator.Infrastructure.Tests.GoalAcceptanceVerifierTests." +
                "FocusedEvidence_CompatibleBatchReportsUncoveredSelectionAsApparatusFailure",
                "Mcg.AgentOrchestrator.Infrastructure.Tests.ConductorDriverTests.ExecutedMember"
            ]);

        Assert.True(result.Accepted);
        Assert.True(result.Passed);
        Assert.DoesNotContain(result.Checks, check =>
            check.FailureClassification == AcceptanceFailureClassifications.FocusedSelectionApparatusFailure);
    }

    [Xunit.Fact]
    public async Task FocusedEvidence_UnreadableSelectionReceiptReportsDistinctApparatusCause()
    {
        var (result, _) = await RunMappedEvidenceAsync(
            "Infrastructure.Tests: ConductorDriverTests; " +
            "Infrastructure.Tests: GateReadyCandidateProjectorTests",
            executedTestCount: 2,
            corruptTrx: true);

        Assert.True(result.Accepted);
        Assert.False(result.Passed);
        Assert.Equal(FindingEvidenceOutcomeReason.ApparatusFailure, result.OutcomeReason);
        var check = Assert.Single(result.Checks);
        Assert.Equal(AcceptanceFailureClassifications.FocusedSelectionReceiptUnreadable, check.FailureClassification);
        Assert.Contains("could not read test receipt", check.OutputTail, StringComparison.Ordinal);
        Assert.DoesNotContain("matching 0 tests", check.OutputTail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task FocusedEvidence_NativeProjectRequest_ReportsProjectMode()
    {
        var (nativeProjectResult, _) = await RunMappedEvidenceAsync("Infrastructure.Tests: mapped-project");
        Assert.True(nativeProjectResult.Accepted);
        Assert.True(nativeProjectResult.Passed);
        Assert.Equal(
            "reviewer mapped project evidence: Infrastructure.Tests",
            nativeProjectResult.Checks.Single().Name);
        Assert.Contains("mode=project reason=explicit-mapped-project-request", nativeProjectResult.Summary);
    }

    [Xunit.Fact]
    public void FocusedEvidence_ExtractedInfrastructureAliasesResolveFromRegisteredMtpProjects()
    {
        const string providerProject =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/" +
            "Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj";
        const string secondProject =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SecondModule/" +
            "Mcg.AgentOrchestrator.Infrastructure.SecondModule.Tests.csproj";
        var settings = new AcceptanceGateEngineSettings
        {
            MtpInvocations =
            [
                new AcceptanceMtpInvocation { Project = providerProject },
                new AcceptanceMtpInvocation { Project = secondProject }
            ]
        };

        Assert.True(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject(
            "Infrastructure.ProviderEnvironment.Tests",
            settings,
            out var resolved));
        Assert.Equal(providerProject, resolved);
        Assert.Equal("Infrastructure.ProviderEnvironment.Tests", GoalAcceptanceVerifier.ProjectLabel(resolved));
        Assert.True(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject(
            "Infrastructure.SecondModule.Tests",
            settings,
            out resolved));
        Assert.Equal(secondProject, resolved);
    }

    [Xunit.Fact]
    public async Task FocusedEvidence_UmbrellaAliasRoutesMovedClassOnlyToOwningExtractedProject()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "mtpInvocations": [
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": [ "{executable}", "--results-directory", "{resultsDirectory}", "--report-trx-filename", "{trxFileName}", "--minimum-expected-tests", "1" ]
                  },
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": [ "{executable}", "--results-directory", "{resultsDirectory}", "--report-trx-filename", "{trxFileName}", "--minimum-expected-tests", "1" ]
                  }
                ]
              },
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var providerDirectory = Path.Combine(
            root,
            "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "ProviderEnvironment");
        Directory.CreateDirectory(providerDirectory);
        File.WriteAllText(
            Path.Combine(providerDirectory, "ProviderDefaultTests.cs"),
            "sealed class ProviderDefaultTests { }");
        var goalId = new GoalId(Guid.NewGuid().ToString("N"));
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        8,
                        "Minimum expected tests was set to 1, but 0 tests were selected."));
                }

                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests"))
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunFocusedEvidenceAsync(
                root,
                goalId,
                "Infrastructure.Tests: FullyQualifiedName~ProviderDefaultTests");

            Assert.True(result.Accepted);
            Assert.True(result.Passed);
            var check = Assert.Single(result.Checks);
            Assert.Contains("Infrastructure.ProviderEnvironment.Tests", check.Name, StringComparison.Ordinal);
            Assert.DoesNotContain(calls, call => IsMtpExecutableCall(
                call,
                "Mcg.AgentOrchestrator.Infrastructure.Tests"));
            Assert.Single(calls, call => IsMtpExecutableCall(
                call,
                "Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests"));
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task FocusedEvidence_UmbrellaAliasSplitsParentAndMovedClassesByOwningProject()
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "mtpInvocations": [
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": [ "{executable}", "--results-directory", "{resultsDirectory}", "--report-trx-filename", "{trxFileName}", "--minimum-expected-tests", "1" ]
                  },
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderEnvironment/Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments": [ "{executable}", "--results-directory", "{resultsDirectory}", "--report-trx-filename", "{trxFileName}", "--minimum-expected-tests", "1" ]
                  }
                ]
              },
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var providerDirectory = Path.Combine(
            root,
            "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "ProviderEnvironment");
        Directory.CreateDirectory(providerDirectory);
        File.WriteAllText(
            Path.Combine(providerDirectory, "ProviderDefaultTests.cs"),
            "sealed class ProviderDefaultTests { }");
        var goalId = new GoalId(Guid.NewGuid().ToString("N"));
        try
        {
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests") ||
                    IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests"))
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunFocusedEvidenceAsync(
                root,
                goalId,
                "Infrastructure.Tests: GoalAcceptanceVerifierTests,ProviderDefaultTests");

            Assert.True(result.Accepted);
            Assert.True(result.Passed);
            Assert.Equal(2, result.Checks.Count);
            var target = Assert.Single(result.Coverage?.TargetToChecks ?? []);
            Assert.Equal(2, target.CheckNames.Count);
            Assert.Single(calls, call => IsMtpExecutableCall(
                call,
                "Mcg.AgentOrchestrator.Infrastructure.Tests"));
            Assert.Single(calls, call => IsMtpExecutableCall(
                call,
                "Mcg.AgentOrchestrator.Infrastructure.ProviderEnvironment.Tests"));
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task FocusedEvidence_MixedRequest_ReportsFocusedAndMappedProjectReason()
    {
        var (result, calls) = await RunMappedEvidenceAsync(
            "Infrastructure.Tests: AlphaTests; Core.Tests: mapped-project");

        Assert.True(result.Accepted);
        Assert.True(result.Passed);
        Assert.Equal(
            [
                "reviewer focused evidence: Infrastructure.Tests FullyQualifiedName~AlphaTests",
                "reviewer mapped project evidence: Core.Tests"
            ],
            result.Checks.Select(check => check.Name));
        Assert.Contains(
            "mode=mixed reason=explicit-focused-and-mapped-project-request",
            result.Summary);
        Assert.Equal("mixed", result.Coverage?.ExecutionMode);
        Assert.Equal("explicit-focused-and-mapped-project-request", result.Coverage?.ExecutionReason);
        var testCalls = calls
            .Where(call =>
                IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Core.Tests") ||
                IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            .ToArray();
        Assert.Equal(2, testCalls.Length);
        Assert.Contains("--filter-class", testCalls[0]);
        Assert.DoesNotContain("--filter-class", testCalls[1]);
    }

    [Xunit.Fact]
    public async Task FocusedEvidence_EmptyRequest_IsRejectedWithoutRunnerCall()
    {
        var (emptyResult, calls) = await RunMappedEvidenceAsync(" ; ");
        Assert.False(emptyResult.Accepted);
        Assert.False(emptyResult.Passed);
        Assert.Equal("empty evidence request", emptyResult.Summary);
        Assert.Empty(emptyResult.Checks);
        Assert.Empty(calls);
    }

    [Xunit.Fact]
    public async Task FocusedEvidence_OverCapWithBadAlias_RejectsAlias()
    {
        var (result, calls) = await RunMappedEvidenceAsync(
            "Infrastructure.Tests: AlphaTests,BetaTests,GammaTests,DeltaTests,EpsilonTests; Unknown.Tests: ZetaTests");

        Assert.False(result.Accepted);
        Assert.False(result.Passed);
        Assert.Equal("unsupported evidence request project alias 'Unknown.Tests'", result.Summary);
        Assert.Empty(result.Checks);
        Assert.Empty(calls);
    }

    [Xunit.Fact]
    public async Task FocusedEvidence_OverCapWithBadFilter_RejectsFilter()
    {
        var (result, calls) = await RunMappedEvidenceAsync(
            "Infrastructure.Tests: AlphaTests,BetaTests,GammaTests,DeltaTests,EpsilonTests; Core.Tests: all");

        Assert.False(result.Accepted);
        Assert.False(result.Passed);
        Assert.Contains("unbounded evidence request rejected", result.Summary);
        Assert.Empty(result.Checks);
        Assert.Empty(calls);
    }

    [Xunit.Fact]
    public async Task FocusedEvidence_DuplicateProjectAliases_BatchCompatibleSelectionsInOneCheck()
    {
        var (result, calls) = await RunMappedEvidenceAsync(
            "Infrastructure.Tests: AlphaTests,BetaTests,GammaTests; Mcg.AgentOrchestrator.Infrastructure.Tests: DeltaTests,EpsilonTests",
            executedTestCount: 5,
            executedTestIdentities:
            [
                "Mcg.AgentOrchestrator.Infrastructure.Tests.AlphaTests.ExecutedMember",
                "Mcg.AgentOrchestrator.Infrastructure.Tests.BetaTests.ExecutedMember",
                "Mcg.AgentOrchestrator.Infrastructure.Tests.GammaTests.ExecutedMember",
                "Mcg.AgentOrchestrator.Infrastructure.Tests.DeltaTests.ExecutedMember",
                "Mcg.AgentOrchestrator.Infrastructure.Tests.EpsilonTests.ExecutedMember"
            ]);

        Assert.True(result.Accepted);
        Assert.True(result.Passed);
        var check = Assert.Single(result.Checks);
        Assert.Single(calls, call =>
            IsMtpExecutableCall(call, "Mcg.AgentOrchestrator.Infrastructure.Tests"));
        Assert.StartsWith(
            "reviewer focused evidence: Infrastructure.Tests ",
            check.Name,
            StringComparison.Ordinal);
        Assert.Contains("AlphaTests", check.Name, StringComparison.Ordinal);
        Assert.Contains("EpsilonTests", check.Name, StringComparison.Ordinal);
        Assert.Contains("compatible-same-project-batch", result.Summary, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task FocusedEvidence_WideFilteredCheckTimesOut_UsesManifestBudgetAndReportsMode()
    {
        var observedTimeouts = new List<TimeSpan>();
        var (result, _) = await RunMappedEvidenceAsync(
            "Infrastructure.Tests: AlphaTests,BetaTests,GammaTests,DeltaTests,EpsilonTests",
            timeOutTests: true,
            observedTimeouts: observedTimeouts);

        Assert.True(result.Accepted);
        Assert.False(result.Passed);
        Assert.Contains(TimeSpan.FromMinutes(40), observedTimeouts);
        var check = Assert.Single(result.Checks);
        Assert.False(check.Passed);
        Assert.Contains("reviewer-focused-evidence", check.Name);
        Assert.Contains("mode=focused reason=explicit-focused-mapping", result.Summary);
    }

    [Xunit.Fact]
    public void FocusedEvidence_RealCheckThatPassesOnBothArmsIsVacuous()
    {
        var outcome = GoalAcceptanceVerifier.ClassifyFocusedEvidenceExperiment(
            CreateFocusedEvidenceArm(FindingEvidenceArm.Candidate, FindingEvidenceArmDisposition.Green),
            CreateFocusedEvidenceArm(FindingEvidenceArm.Baseline, FindingEvidenceArmDisposition.Green));

        Assert.Equal(FindingEvidenceOutcomeReason.VacuousEvidence, outcome);
    }

    [Xunit.Fact]
    public async Task FocusedEvidence_RealCheckThatFailsOnBaselineIsValid()
    {
        var valid = await RunRealDualArmProbeAsync(
            baselineFeature: "public static class Feature { public static bool Enabled => false; }",
            candidateFeature: "public static class Feature { public static bool Enabled => true; }");
        Assert.Equal(FindingEvidenceOutcomeReason.ValidEvidence, valid.OutcomeReason);
        Assert.Equal(FindingEvidenceArmDisposition.Green, valid.Arms![0].Disposition);
        Assert.Equal(FindingEvidenceArmDisposition.Red, valid.Arms[1].Disposition);
        Assert.NotEmpty(valid.Arms[1].Checks.SelectMany(check => check.FailingTestIdentities ?? []));
    }

    [Xunit.Fact]
    public async Task FocusedEvidence_RealBaselineBuildFailureIsInconclusive()
    {
        var inconclusive = await RunRealDualArmProbeAsync(
            baselineFeature: "public static class Feature { public static bool Enabled => ; }",
            candidateFeature: "public static class Feature { public static bool Enabled => true; }");
        Assert.Equal(FindingEvidenceOutcomeReason.BaselineInconclusive, inconclusive.OutcomeReason);
        Assert.Equal(FindingEvidenceArmDisposition.Green, inconclusive.Arms![0].Disposition);
        Assert.Equal(FindingEvidenceArmDisposition.Inconclusive, inconclusive.Arms[1].Disposition);
        Assert.Empty(inconclusive.Arms[1].Checks.SelectMany(check => check.FailingTestIdentities ?? []));
    }

    private static async Task<FocusedEvidenceRunResult> RunRealDualArmProbeAsync(
        string baselineFeature,
        string candidateFeature,
        string? candidateMarker = null)
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "slotCount": 1,
                "maxConcurrentShards": 1,
                "enforceStructuralCoverage": false,
                "mtpInvocations": [
                  {
                    "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj",
                    "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "arguments": [
                      "{executable}",
                      "--results-directory",
                      "{resultsDirectory}",
                      "--report-trx",
                      "--report-trx-filename",
                      "{trxFileName}"
                    ]
                  }
                ]
              },
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        File.WriteAllText(
            Path.Combine(root, "Directory.Build.props"),
            """
            <Project>
              <PropertyGroup>
                <UseSharedCompilation>false</UseSharedCompilation>
                <RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources>
                <NuGetAudit>false</NuGetAudit>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(root, "NuGet.Config"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
              </packageSources>
            </configuration>
            """);
        var projectDirectory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Core.Tests");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(
            Path.Combine(projectDirectory, "Mcg.AgentOrchestrator.Core.Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
                <IsTestProject>true</IsTestProject>
                <OutputType>Exe</OutputType>
                <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="2.3.2" />
                <PackageReference Include="xunit.v3.mtp-v2" Version="3.2.2" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(projectDirectory, "DualArmProbeTests.cs"),
            """
            public sealed class DualArmProbeTests
            {
                [Xunit.Fact]
                public void CandidateBehaviorIsPresent() => Xunit.Assert.True(Feature.Enabled);
            }
            """);
        var featurePath = Path.Combine(projectDirectory, "Feature.cs");
        File.WriteAllText(featurePath, baselineFeature);
        AssertGitSucceeded(root, "init", "-b", "main");
        AssertGitSucceeded(root, "config", "user.email", "dual-arm@example.invalid");
        AssertGitSucceeded(root, "config", "user.name", "Dual Arm Fixture");
        AssertGitSucceeded(root, "add", ".");
        AssertGitSucceeded(root, "commit", "-m", "baseline");
        AssertGitSucceeded(root, "checkout", "-b", "goal/dual-arm");
        File.WriteAllText(featurePath, candidateFeature);
        if (candidateMarker is not null)
        {
            File.WriteAllText(Path.Combine(root, "candidate-marker.txt"), candidateMarker);
        }
        AssertGitSucceeded(root, "add", ".");
        AssertGitSucceeded(root, "commit", "-m", "candidate");

        var goalId = GoalId.New();
        FocusedEvidenceRunResult? result = null;
        try
        {
            result = await new GoalAcceptanceVerifier().RunFocusedEvidenceAsync(
                root,
                goalId,
                "Core.Tests: DualArmProbeTests",
                runBaselineArm: true);
            var baseline = Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.Baseline);
            Assert.All(
                baseline.Checks.Where(check => !string.IsNullOrWhiteSpace(check.ArtifactsPath)),
                check => Assert.False(
                    Directory.Exists(check.ArtifactsPath!),
                    $"Baseline artifacts were retained at {check.ArtifactsPath}."));
            return result;
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            foreach (var artifactPath in result?.Arms?
                         .SelectMany(arm => arm.Checks)
                         .Select(check => check.ArtifactsPath)
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Distinct(StringComparer.OrdinalIgnoreCase) ?? [])
            {
                if (Directory.Exists(artifactPath))
                {
                    TryDeleteDirectoryWithRetry(artifactPath!);
                }
            }
            TryDeleteDirectoryWithRetry(root);
        }
    }

    private static void TryDeleteDirectoryWithRetry(string path)
    {
        try
        {
            DeleteDirectoryWithRetry(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Probe teardown must never replace the assertion or execution failure being diagnosed.
        }
    }

    private static void AssertGitSucceeded(string workingDirectory, params string[] arguments)
    {
        var result = GitCli.Run(workingDirectory, arguments);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)} failed: {result.Error}");
    }

    private static async Task<(FocusedEvidenceRunResult Result, List<string[]> Calls)> RunMappedEvidenceAsync(
        string request,
        bool timeOutTests = false,
        List<TimeSpan>? observedTimeouts = null,
        Action<string>? configureWorkspace = null,
        int? executedTestCount = null,
        int testExitCode = 0,
        Func<string, IDisposable?>? holdWorkspaceResource = null,
        IReadOnlyList<string>? executedTestIdentities = null,
        bool corruptTrx = false)
    {
        var calls = new List<string[]>();
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var manifestPath = Path.Combine(root, "config", "acceptance-manifest.json");
        var configuredManifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        configuredManifest["engine"]!["timeouts"]!["defaultMinutes"] = 40;
        File.WriteAllText(manifestPath, configuredManifest.ToJsonString());
        configureWorkspace?.Invoke(root);
        var goalId = new GoalId(Guid.NewGuid().ToString("N"));
        try
        {
            using var workspaceResource = holdWorkspaceResource?.Invoke(root);
            var verifier = new GoalAcceptanceVerifier((args, _, timeout, _) =>
            {
                calls.Add(args);
                observedTimeouts?.Add(timeout);
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Core.Tests") ||
                    IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    if (timeOutTests)
                    {
                        return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                            124,
                            "timed out",
                            TimedOut: true,
                            Timeout: TimeSpan.FromMinutes(40),
                            Elapsed: TimeSpan.FromMinutes(40)));
                    }

                    WriteMtpTrx(args, executedTestCount, executedTestIdentities);
                    if (corruptTrx)
                    {
                        var resultsDirectoryIndex = Array.IndexOf(args, "--results-directory");
                        var trxFileIndex = Array.IndexOf(args, "--report-trx-filename");
                        File.WriteAllText(
                            Path.Combine(args[resultsDirectoryIndex + 1], args[trxFileIndex + 1]),
                            "<not-valid-trx");
                    }
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        testExitCode,
                        executedTestCount == 0
                            ? "Minimum expected tests was set to 1, but 0 tests were selected."
                            : "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunFocusedEvidenceAsync(root, goalId, request);
            return (result, calls);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
        }
    }

    private static FocusedEvidenceArmRunResult CreateFocusedEvidenceArm(
        FindingEvidenceArm arm,
        FindingEvidenceArmDisposition disposition) =>
        new(
            arm,
            $"{arm.ToString().ToLowerInvariant()}-sha",
            disposition,
            Accepted: true,
            Passed: disposition == FindingEvidenceArmDisposition.Green,
            Summary: disposition.ToString(),
            Checks: []);

}

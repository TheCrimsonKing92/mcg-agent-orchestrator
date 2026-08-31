using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

public sealed class HermesAcpTrialTests
{
    [Xunit.Fact]
    public void BuiltInProviderIsTypedNonCommittingAndNotInDefaultProfileCatalog()
    {
        var provider = WorkerProviderCatalog.Default().ResolveProfile("hermes-acp");

        Assert.NotEqual(ProviderKind.Unknown, provider.Identity.Kind);
        Assert.False(provider.Capabilities.CanSelfCommit);
        Assert.False(provider.Capabilities.CanSelfVerify);
        Assert.False(provider.Capabilities.SupportsInteractiveSession);
        Assert.True(provider.Capabilities.SupportsPlanMode);
        Assert.DoesNotContain(
            WorkerProfileCatalog.Default().Profiles,
            profile => profile.Name.Equals("hermes-acp", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact]
    public void HermesUsesWholeProcessSandboxForEveryRoleAndFreshDedicatedHome()
    {
        var provider = WorkerProviderCatalog.Default().ResolveProfile("hermes-acp");
        Assert.Equal(WorkerSandboxProvider.Hermes, BackgroundDispatchRunner.ResolveSandboxProvider(provider));
        Assert.All(
            Enum.GetValues<AgentRole>(),
            role => Assert.True(BackgroundDispatchRunner.ShouldUseOsSandbox(true, false, role, WorkerSandboxProvider.Hermes)));

        using var fixture = new Fixture();
        var first = new ProcessStartInfo();
        var second = new ProcessStartInfo();
        DispatchProcessHost.SeedHermesEnvironment(first, fixture.Sandbox);
        DispatchProcessHost.SeedHermesEnvironment(second, fixture.Sandbox);

        Assert.NotEqual(first.Environment["HERMES_HOME"], second.Environment["HERMES_HOME"]);
        Assert.StartsWith(fixture.Sandbox, first.Environment["HERMES_HOME"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1", first.Environment["HERMES_ACP_SKIP_CONFIGURED_MCP"]);
    }

    [Xunit.Fact]
    public void AdapterReadsPromptFilePinsIdentityAndKeepsPromptOffArgv()
    {
        using var fixture = new Fixture(useUnicodePath: true);
        var prompt = Path.Combine(fixture.Workspace, "brief with spaces.md");
        var content = "literal `$(not-executed)` prompt";
        File.WriteAllText(prompt, content, new UTF8Encoding(false));
        var digest = Sha256(content);
        var request = new HermesAcpRequest(prompt, digest, fixture.Workspace, fixture.Sandbox, "gpt-test", "OpenAI");

        var plan = new HermesAcpAdapter().Prepare(request);

        Assert.Equal(digest, plan.PromptSha256);
        Assert.Equal(Encoding.UTF8.GetBytes(content), plan.PromptBytes);
        Assert.Equal(HermesAcpAdapter.PinnedRelease, plan.PinnedRelease);
        Assert.Equal(HermesAcpAdapter.PinnedCommit, plan.PinnedCommit);
        Assert.Equal("hermes", plan.StartInfo.FileName);
        Assert.Equal(["--safe-mode", "acp"], plan.StartInfo.ArgumentList);
        Assert.DoesNotContain(content, string.Join(' ', plan.StartInfo.ArgumentList), StringComparison.Ordinal);
        Assert.StartsWith(fixture.Sandbox, plan.HermesHome, StringComparison.OrdinalIgnoreCase);
        Assert.True(plan.StartInfo.RedirectStandardInput);
        Assert.True(plan.StartInfo.RedirectStandardOutput);
        Assert.True(plan.StartInfo.RedirectStandardError);
        Assert.False(plan.StartInfo.UseShellExecute);
        Assert.True(plan.StartInfo.CreateNoWindow);
    }

    [Xunit.Fact]
    public void AdapterFailsClosedForOutsidePromptAndIncompleteTerminalReceipt()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "outside.md");
        File.WriteAllText(outside, "outside");
        var request = new HermesAcpRequest(outside, Sha256("outside"), fixture.Workspace, fixture.Sandbox, "gpt-test", "OpenAI");

        var outsideFailure = Assert.Throws<InvalidOperationException>(() => new HermesAcpAdapter().Prepare(request));
        Assert.Contains("inside the assigned worktree", outsideFailure.Message, StringComparison.Ordinal);

        var prompt = Path.Combine(fixture.Workspace, "brief.md");
        File.WriteAllText(prompt, "inside");
        request = request with { PromptPath = prompt, ExpectedPromptSha256 = Sha256("inside") };
        var receipt = new HermesAcpTerminalReceipt(
            request.ExpectedPromptSha256,
            request.ExpectedModel,
            request.ExpectedProvider,
            InputTokens: 0,
            OutputTokens: 0,
            Completed: true,
            ExitCode: 0,
            CancellationOrShutdownAcknowledged: true,
            JobExitConfirmed: true,
            StandardErrorSha256: new string('0', 64),
            PermissionPolicyViolated: false,
            UnexpectedChild: false,
            FinalOutput: SuccessfulWorkerResult);

        var usageFailure = Assert.Throws<InvalidOperationException>(() => HermesAcpAdapter.ValidateTerminalReceipt(request, receipt));
        Assert.Contains("non-zero usage", usageFailure.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void DecisionEngineNeverAdoptsIncompleteEvidenceAndRejectsImmediateFailure()
    {
        var incomplete = HermesTrialDecisionEngine.Evaluate(new HermesTrialEvidence());
        Assert.Equal(HermesTrialDisposition.TrialOnly, incomplete.Disposition);
        Assert.Contains(incomplete.Reasons, reason => reason.StartsWith("prompt-probes:", StringComparison.Ordinal));

        var escaped = HermesTrialDecisionEngine.Evaluate(CompleteEvidence() with { ContainmentEscape = true });
        Assert.Equal(HermesTrialDisposition.Rejected, escaped.Disposition);
        Assert.Contains("containment-escape", escaped.Reasons);
    }

    [Xunit.Fact]
    public void DecisionEngineEmitsOnlyOperatorEligibilityWhenAllGatesPass()
    {
        var decision = HermesTrialDecisionEngine.Evaluate(CompleteEvidence());

        Assert.Equal(HermesTrialDisposition.EligibleForOperatorAdoption, decision.Disposition);
        Assert.Empty(decision.Reasons);
        Assert.True(decision.MedianVelocityImprovement >= 0.15);
    }

    private static HermesTrialEvidence CompleteEvidence() => new()
    {
        PromptProbeCount = 20,
        MatchingPromptDigests = 20,
        ParseableWorkerResults = 19,
        FalseCompletes = 0,
        OutsideWriteDeniedAfterInRootControl = true,
        LifecycleCycles = 10,
        LifecycleCyclesWithUsage = 10,
        UnicodeAndSpacesPathPassed = true,
        PairedTaskCount = 8,
        HermesAcceptanceLosses = 1,
        BaselineReviewReadySeconds = [100, 100, 100, 100, 100, 100, 100, 100],
        HermesReviewReadySeconds = [80, 80, 80, 80, 80, 80, 80, 80],
        BaselineInterventions = 8,
        HermesInterventions = 6,
        BaselineCost = 10,
        HermesCost = 10.5m,
        BaselineTokens = 10_000,
        HermesTokens = 10_500
    };

    private static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private const string SuccessfulWorkerResult = """
        WORKER_RESULT:
        files: none
        commands: none
        tests: pass - fake receipt
        commit: none
        blockers: none
        model_fit: fake/test - adequate - test - deterministic
        skills: none
        confidence: high
        END_WORKER_RESULT
        """;

    private sealed class Fixture : IDisposable
    {
        public Fixture(bool useUnicodePath = false)
        {
            Root = Path.Combine(Path.GetTempPath(), "mcg-hermes-tests", $"{(useUnicodePath ? "späce 路径" : "plain")}-{Guid.NewGuid():N}");
            Workspace = Path.Combine(Root, "workspace");
            Sandbox = Path.Combine(Root, "sandbox");
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(Sandbox);
        }

        public string Root { get; }
        public string Workspace { get; }
        public string Sandbox { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}

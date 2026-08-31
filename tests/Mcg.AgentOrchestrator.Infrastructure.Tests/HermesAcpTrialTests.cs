using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Cli;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed class HermesAcpTrialTests
{
    [Xunit.Fact]
    public void AppRegistersOperatorTriggerableHermesAcpLifecycleCommand()
    {
        Assert.Contains("hermes-acp-trial", CliArgumentParser.RecognizedCommands);
    }

    [Xunit.Fact]
    public async Task AppCommandWiresPromptAndReceiptToHermesLifecycle()
    {
        using var fixture = new Fixture();
        var promptPath = Path.Combine(fixture.Workspace, "brief.md");
        File.WriteAllText(promptPath, "operator trial prompt");
        var digest = Sha256("operator trial prompt");
        var receiptPath = Path.Combine(fixture.Sandbox, "terminal.json");
        HermesAcpRequest? observedRequest = null;
        string? observedReceiptPath = null;
        var output = new StringWriter();
        var error = new StringWriter();

        var receipt = await HermesAcpCliCommand.ExecuteAsync(
            [
                "hermes-acp-trial",
                "--confirm-live-hermes-start",
                "--prompt", promptPath,
                "--prompt-sha256", digest,
                "--workspace", fixture.Workspace,
                "--sandbox", fixture.Sandbox,
                "--provider", "OpenAI",
                "--model", "gpt-test",
                "--role", "Developer",
                "--receipt", receiptPath
            ],
            output,
            error,
            (request, path, _, _) =>
            {
                observedRequest = request;
                observedReceiptPath = path;
                return Task.FromResult(ValidReceipt(request));
            },
            () => Path.Combine(fixture.Root, ".trial-state", "harness"),
            () => null);

        Assert.NotNull(observedRequest);
        Assert.Equal(promptPath, observedRequest.PromptPath);
        Assert.Equal(digest, observedRequest.ExpectedPromptSha256);
        Assert.Equal(AgentRole.Developer, observedRequest.Role);
        Assert.Equal(receiptPath, observedReceiptPath);
        Assert.Equal(SuccessfulWorkerResult.Trim(), output.ToString().Trim());
        Assert.DoesNotContain("terminal receipt", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(receiptPath, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SuccessfulWorkerResult, receipt.FinalOutput);
    }

    [Xunit.Fact]
    public async Task AppCommandRejectsStartOutsideContainedTrialRoot()
    {
        using var fixture = new Fixture();
        var promptPath = Path.Combine(fixture.Workspace, "brief.md");
        File.WriteAllText(promptPath, "operator trial prompt");
        var receiptPath = Path.Combine(fixture.Sandbox, "terminal.json");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => HermesAcpCliCommand.ExecuteAsync(
            [
                "hermes-acp-trial",
                "--confirm-live-hermes-start",
                "--prompt", promptPath,
                "--prompt-sha256", Sha256("operator trial prompt"),
                "--workspace", fixture.Workspace,
                "--sandbox", fixture.Sandbox,
                "--provider", "OpenAI",
                "--model", "gpt-test",
                "--role", "Developer",
                "--receipt", receiptPath
            ],
            TextWriter.Null,
            TextWriter.Null,
            (request, _, _, _) => Task.FromResult(ValidReceipt(request))));

        Assert.Contains("contained trial root", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public async Task LifecycleDrivesJsonRpcAndPersistsValidatedTerminalReceipt()
    {
        using var fixture = new Fixture();
        var promptPath = Path.Combine(fixture.Workspace, "brief.md");
        File.WriteAllText(promptPath, "protocol prompt");
        var request = new HermesAcpRequest(
            promptPath,
            Sha256("protocol prompt"),
            fixture.Workspace,
            fixture.Sandbox,
            "gpt-test",
            "OpenAI",
            AgentRole.Developer);
        var version = new FakeHermesProcess(
            $"Hermes {HermesAcpAdapter.PinnedRelease} {HermesAcpAdapter.PinnedCommit}");
        var protocol = string.Join(Environment.NewLine,
        [
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, result = new { protocolVersion = 1 } }),
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "session/update",
                @params = new { sessionId = "session-1", update = new { sessionUpdate = "available_commands_update" } }
            }),
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                result = new { sessionId = "session-1", models = new { currentModelId = "OpenAI:gpt-test" } }
            }),
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 99,
                method = "session/request_permission",
                @params = new
                {
                    sessionId = "session-1",
                    toolCall = new
                    {
                        kind = "edit",
                        locations = new[] { new { path = Path.Combine(fixture.Workspace, "changed.txt") } }
                    },
                    options = new[]
                    {
                        new { optionId = "allow", kind = "allow_once" },
                        new { optionId = "deny", kind = "reject_once" }
                    }
                }
            }),
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "session/update",
                @params = new
                {
                    sessionId = "session-1",
                    update = new
                    {
                        sessionUpdate = "agent_message_chunk",
                        content = new { type = "text", text = SuccessfulWorkerResult }
                    }
                }
            }),
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 3,
                result = new
                {
                    stopReason = "end_turn",
                    usage = new { inputTokens = 12, outputTokens = 8, totalTokens = 20 }
                }
            }),
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "session/update",
                @params = new { sessionId = "session-1", update = new { sessionUpdate = "available_commands_update" } }
            })
        ]);
        var acp = new FakeHermesProcess(protocol);
        var launcher = new FakeHermesProcessLauncher(version, acp);
        var receiptPath = Path.Combine(fixture.Sandbox, "terminal-receipt.json");

        var receipt = await new HermesAcpLifecycle(launcher: launcher).RunAsync(
            request,
            receiptPath,
            TextWriter.Null);

        Assert.True(receipt.Completed);
        Assert.Equal(12, receipt.InputTokens);
        Assert.Equal(8, receipt.OutputTokens);
        Assert.Equal("session-1", receipt.SessionId);
        Assert.True(receipt.JobExitConfirmed);
        Assert.False(receipt.PermissionPolicyViolated);
        Assert.False(receipt.UnexpectedChild);
        Assert.True(File.Exists(receiptPath));
        var persisted = JsonSerializer.Deserialize<HermesAcpTerminalReceipt>(
            File.ReadAllText(receiptPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(receipt, persisted);
        Assert.Equal(2, launcher.StartInfos.Count);
        Assert.Equal(["--version"], launcher.StartInfos[0].ArgumentList);
        Assert.Equal(["--safe-mode", "acp"], launcher.StartInfos[1].ArgumentList);
        Assert.Contains("\"method\":\"initialize\"", acp.Input, StringComparison.Ordinal);
        Assert.Contains("\"readTextFile\":false", acp.Input, StringComparison.Ordinal);
        Assert.Contains("\"writeTextFile\":false", acp.Input, StringComparison.Ordinal);
        Assert.Contains("\"terminal\":false", acp.Input, StringComparison.Ordinal);
        Assert.Contains("\"method\":\"session/new\"", acp.Input, StringComparison.Ordinal);
        Assert.Contains("\"method\":\"session/prompt\"", acp.Input, StringComparison.Ordinal);
        Assert.Contains("protocol prompt", acp.Input, StringComparison.Ordinal);
        Assert.Contains("\"optionId\":\"allow\"", acp.Input, StringComparison.Ordinal);
        Assert.True(acp.InputCompleted);
        Assert.True(acp.OutputReachedEnd);
        Assert.False(acp.Killed);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer, true, "allow", false)]
    [Xunit.InlineData(AgentRole.Planner, true, "deny", false)]
    [Xunit.InlineData(AgentRole.Developer, false, "deny", true)]
    public async Task JsonRpcScopesPermissionToWriteCapableRoleAndWorkspace(
        AgentRole role,
        bool insideWorkspace,
        string expectedOption,
        bool expectedPolicyViolation)
    {
        using var fixture = new Fixture();
        var requestedPath = insideWorkspace
            ? Path.Combine(fixture.Workspace, "changed.txt")
            : Path.Combine(fixture.Root, "outside.txt");
        var input = new StringWriter();
        var output = new StringReader(string.Join(Environment.NewLine,
        [
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 99,
                method = "session/request_permission",
                @params = new
                {
                    sessionId = "session-1",
                    toolCall = new
                    {
                        kind = "edit",
                        locations = new[] { new { path = requestedPath } }
                    },
                    options = new[]
                    {
                        new { optionId = "allow", kind = "allow_once" },
                        new { optionId = "deny", kind = "reject_once" }
                    }
                }
            }),
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, result = new { stopReason = "end_turn" } })
        ]));
        var rpc = new HermesAcpJsonRpcClient(
            input,
            output,
            TextWriter.Null,
            role,
            fixture.Workspace);

        _ = await rpc.CallAsync("session/prompt", new { }, TestContext.Current.CancellationToken);

        Assert.Contains($"\"optionId\":\"{expectedOption}\"", input.ToString(), StringComparison.Ordinal);
        Assert.Equal(expectedPolicyViolation, rpc.PermissionPolicyViolated);
    }

    [Xunit.Fact]
    public async Task JsonRpcUnknownPermissionWithoutLocationFailsClosed()
    {
        using var fixture = new Fixture();
        var input = new StringWriter();
        var output = PermissionRequestOutput("unknown-capability");
        var rpc = new HermesAcpJsonRpcClient(
            input,
            output,
            TextWriter.Null,
            AgentRole.Developer,
            fixture.Workspace);

        _ = await rpc.CallAsync("session/prompt", new { }, TestContext.Current.CancellationToken);

        Assert.Contains("\"optionId\":\"deny\"", input.ToString(), StringComparison.Ordinal);
        Assert.True(rpc.PermissionPolicyViolated);
    }

    [Xunit.Fact]
    public async Task JsonRpcTerminalPermissionWithoutLocationFailsClosed()
    {
        using var fixture = new Fixture();
        var developerInput = new StringWriter();
        var developerRpc = new HermesAcpJsonRpcClient(
            developerInput,
            PermissionRequestOutput("execute"),
            TextWriter.Null,
            AgentRole.Developer,
            fixture.Workspace);
        var plannerInput = new StringWriter();
        var plannerRpc = new HermesAcpJsonRpcClient(
            plannerInput,
            PermissionRequestOutput("execute"),
            TextWriter.Null,
            AgentRole.Planner,
            fixture.Workspace);

        _ = await developerRpc.CallAsync("session/prompt", new { }, TestContext.Current.CancellationToken);
        _ = await plannerRpc.CallAsync("session/prompt", new { }, TestContext.Current.CancellationToken);

        Assert.Contains("\"optionId\":\"deny\"", developerInput.ToString(), StringComparison.Ordinal);
        Assert.True(developerRpc.PermissionPolicyViolated);
        Assert.Contains("\"optionId\":\"deny\"", plannerInput.ToString(), StringComparison.Ordinal);
        Assert.False(plannerRpc.PermissionPolicyViolated);
    }

    [Xunit.Fact]
    public async Task ProcessLauncherConfirmsOwnedJobExitForRealChild()
    {
        if (!OperatingSystem.IsWindows()) return;

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Arguments = "/d /c exit 0"
        };

        using var process = new HermesAcpProcessLauncher().Start(startInfo);
        process.CompleteInput();
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var stdoutText = await stdout;
        var stderrText = await stderr;

        Assert.True(
            process.ExitCode == 0,
            $"Expected exit 0, observed {process.ExitCode}. stdout='{stdoutText}' stderr='{stderrText}'.");
        Assert.True(process.JobExitConfirmed);
        Assert.True(process.SurvivorInventoryEmpty);
    }

    [Xunit.Fact]
    public async Task ProcessLauncherInventoriesAndKillsOwnedChild()
    {
        if (!OperatingSystem.IsWindows()) return;

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"$child = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList '-NoLogo -NoProfile -NonInteractive -Command Start-Sleep -Seconds 30'; [Console]::Out.WriteLine($child.Id)\""
        };

        using var process = new HermesAcpProcessLauncher().Start(startInfo);
        process.CompleteInput();
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var childLine = await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken);
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(int.TryParse(childLine, out _), $"Expected child pid, observed '{childLine}'.");
        Assert.False(process.JobExitConfirmed);
        Assert.False(process.SurvivorInventoryEmpty);

        process.Kill();
        _ = await stdout;
        _ = await stderr;
        Assert.True(process.JobExitConfirmed);
        Assert.True(process.SurvivorInventoryEmpty);
    }

    [Xunit.Fact]
    public async Task LifecyclePersistsFailureReceiptAfterConfirmedTeardown()
    {
        using var fixture = new Fixture();
        var promptPath = Path.Combine(fixture.Workspace, "brief.md");
        File.WriteAllText(promptPath, "protocol prompt");
        var request = new HermesAcpRequest(
            promptPath,
            Sha256("protocol prompt"),
            fixture.Workspace,
            fixture.Sandbox,
            "gpt-test",
            "OpenAI",
            AgentRole.Developer);
        var version = new FakeHermesProcess(
            $"Hermes {HermesAcpAdapter.PinnedRelease} {HermesAcpAdapter.PinnedCommit}");
        var protocol = string.Join(Environment.NewLine,
        [
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, result = new { protocolVersion = 1 } }),
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                result = new { sessionId = "session-1", models = new { currentModelId = "Other:hidden-model" } }
            })
        ]);
        var acp = new FakeHermesProcess(protocol, exitCode: 17, jobExitConfirmed: false);
        var receiptPath = Path.Combine(fixture.Sandbox, "terminal-failure.json");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new HermesAcpLifecycle(launcher: new FakeHermesProcessLauncher(version, acp)).RunAsync(
                request,
                receiptPath,
                TextWriter.Null));

        Assert.Contains("hidden model/provider fallback", error.Message, StringComparison.OrdinalIgnoreCase);
        var receipt = JsonSerializer.Deserialize<HermesAcpTerminalReceipt>(
            File.ReadAllText(receiptPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(receipt);
        Assert.False(receipt.Completed);
        Assert.Contains("hidden model/provider fallback", receipt.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.False(receipt.JobExitConfirmed);
        Assert.Equal(17, receipt.ExitCode);
        Assert.True(acp.Killed);
    }

    [Xunit.Fact]
    public void BuiltInProviderIsTypedNonCommittingAndNotInDefaultProfileCatalog()
    {
        var provider = WorkerProviderCatalog.Default().ResolveProfile("hermes-acp");

        Assert.NotEqual(ProviderKind.Unknown, provider.Identity.Kind);
        Assert.False(provider.Capabilities.CanSelfCommit);
        Assert.False(provider.Capabilities.CanSelfVerify);
        Assert.False(provider.Capabilities.SupportsInteractiveSession);
        Assert.True(provider.Capabilities.SupportsPlanMode);
        Assert.True(WorkerProfileDiagnostics.EvaluatePatchCapability(
            new WorkerProfile("hermes-acp", "mcg-orchestrator hermes-acp-trial --confirm-live-hermes-start"),
            provider).IsPatchCapable);
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

        var prompt = Path.Combine(fixture.Workspace, "brief.md");
        File.WriteAllText(prompt, "same home");
        var request = new HermesAcpRequest(
            prompt,
            Sha256("same home"),
            fixture.Workspace,
            fixture.Sandbox,
            "gpt-test",
            "OpenAI",
            AgentRole.Developer,
            first.Environment["HERMES_HOME"]);

        Assert.Equal(first.Environment["HERMES_HOME"], new HermesAcpAdapter().Prepare(request).HermesHome);
    }

    [Xunit.Fact]
    public void AdapterReadsPromptFilePinsIdentityAndKeepsPromptOffArgv()
    {
        using var fixture = new Fixture(useUnicodePath: true);
        var prompt = Path.Combine(fixture.Workspace, "brief with spaces.md");
        var content = "literal `$(not-executed)` prompt";
        File.WriteAllText(prompt, content, new UTF8Encoding(false));
        var digest = Sha256(content);
        var request = new HermesAcpRequest(
            prompt,
            digest,
            fixture.Workspace,
            fixture.Sandbox,
            "gpt-test",
            "OpenAI",
            AgentRole.Developer);

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
        var request = new HermesAcpRequest(
            outside,
            Sha256("outside"),
            fixture.Workspace,
            fixture.Sandbox,
            "gpt-test",
            "OpenAI",
            AgentRole.Developer);

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

        var survivorFailure = Assert.Throws<InvalidOperationException>(() =>
            HermesAcpAdapter.ValidateTerminalReceipt(request, ValidReceipt(request) with { SurvivorInventoryEmpty = false }));
        Assert.Contains("clean process completion and teardown", survivorFailure.Message, StringComparison.Ordinal);
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

        var digestOvercount = HermesTrialDecisionEngine.Evaluate(CompleteEvidence() with { MatchingPromptDigests = 21 });
        Assert.Equal(HermesTrialDisposition.Rejected, digestOvercount.Disposition);
        Assert.Contains("prompt-digest-mismatch-or-truncation", digestOvercount.Reasons);

        var expandedProbeSet = HermesTrialDecisionEngine.Evaluate(CompleteEvidence() with
        {
            PromptProbeCount = 21,
            MatchingPromptDigests = 21
        });
        Assert.Equal(HermesTrialDisposition.TrialOnly, expandedProbeSet.Disposition);
        Assert.Contains("parseable-worker-results:19/20", expandedProbeSet.Reasons);
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

    private static StringReader PermissionRequestOutput(string kind) => new(string.Join(Environment.NewLine,
    [
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 99,
            method = "session/request_permission",
            @params = new
            {
                sessionId = "session-1",
                toolCall = new { kind },
                options = new[]
                {
                    new { optionId = "allow", kind = "allow_once" },
                    new { optionId = "deny", kind = "reject_once" }
                }
            }
        }),
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, result = new { stopReason = "end_turn" } })
    ]));

    private static HermesAcpTerminalReceipt ValidReceipt(HermesAcpRequest request) => new(
        request.ExpectedPromptSha256,
        request.ExpectedModel,
        request.ExpectedProvider,
        12,
        8,
        Completed: true,
        ExitCode: 0,
        CancellationOrShutdownAcknowledged: true,
        JobExitConfirmed: true,
        StandardErrorSha256: new string('0', 64),
        PermissionPolicyViolated: false,
        UnexpectedChild: false,
        FinalOutput: SuccessfulWorkerResult,
        SessionId: "session-1",
        StopReason: "end_turn",
        PinnedRelease: HermesAcpAdapter.PinnedRelease,
        PinnedCommit: HermesAcpAdapter.PinnedCommit,
        SurvivorInventoryEmpty: true);

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

    private sealed class FakeHermesProcessLauncher(params FakeHermesProcess[] processes) : IHermesAcpProcessLauncher
    {
        private readonly Queue<FakeHermesProcess> _processes = new(processes);
        public List<ProcessStartInfo> StartInfos { get; } = [];

        public IHermesAcpProcess Start(ProcessStartInfo startInfo)
        {
            StartInfos.Add(startInfo);
            return _processes.Dequeue();
        }
    }

    private sealed class FakeHermesProcess(
        string output,
        string error = "",
        int exitCode = 0,
        bool jobExitConfirmed = true,
        bool survivorInventoryEmpty = true) : IHermesAcpProcess
    {
        private readonly StringWriter _input = new();
        private readonly StringReader _output = new(output);
        private readonly StringReader _error = new(error);

        public TextWriter StandardInput => _input;
        public TextReader StandardOutput => _output;
        public TextReader StandardError => _error;
        public int ExitCode { get; } = exitCode;
        public bool JobExitConfirmed { get; } = jobExitConfirmed;
        public bool SurvivorInventoryEmpty { get; } = survivorInventoryEmpty;
        public string Input => _input.ToString();
        public bool OutputReachedEnd { get; private set; }
        public bool InputCompleted { get; private set; }
        public bool Killed { get; private set; }
        public void CompleteInput() => InputCompleted = true;
        public void Kill() => Killed = true;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose()
        {
            OutputReachedEnd = _output.Peek() == -1;
            _input.Dispose();
            _output.Dispose();
            _error.Dispose();
        }
    }
}

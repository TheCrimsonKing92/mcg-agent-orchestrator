using System.Diagnostics;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierTestsFocusedCaptureLimit : GoalAcceptanceVerifierTestBase
{
    private const long CapBytes = 4096;

    [Xunit.Fact]
    public async Task FocusedCommandStopsAtCaptureLimitAndReapsItsChild()
    {
        var root = CreateRoot();
        var owner = CreateFocusedOwner(root);
        var pidPath = Path.Combine(root, "child.pid");
        var check = LoopingCheck(timeoutMinutes: 30, pidPath);
        try
        {
            var verifier = new GoalAcceptanceVerifier();
            var run = verifier.RunInvocationForOwnerTestsAsync(
                owner, owner.CreateInvocation(check.Name), check, Path.GetTempPath(), owner.CancellationToken);
            var result = await run.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.False(result.Result.Passed);
            Assert.StartsWith("acceptance-check-capture-limit:", result.Result.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("acceptance-check-timeout", result.Result.Name, StringComparison.Ordinal);
            AssertProcessExited(int.Parse(File.ReadAllText(pidPath).Trim(), System.Globalization.CultureInfo.InvariantCulture));
            var captureDecision = GoalAcceptanceVerifier.InferPartitionCompletionDecisionForTests(result.Result);
            var timeoutDecision = GoalAcceptanceVerifier.InferPartitionCompletionDecisionForTests(
                result.Result with { Name = result.Result.Name.Replace(
                    "acceptance-check-capture-limit:", "acceptance-check-timeout:", StringComparison.Ordinal) });
            Assert.Equal(AcceptanceShardCompletionPredicates.TimedOut, captureDecision.FailedPredicate);
            Assert.Equal(timeoutDecision.FailedPredicate, captureDecision.FailedPredicate);
            Assert.Equal(timeoutDecision.TimedOut, captureDecision.TimedOut);
        }
        finally
        {
            owner.Cancel();
            await owner.DisposeAsync();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task FocusedProcessMarkerIsDistinctAndChildIsExited()
    {
        var root = CreateRoot();
        using var cancellation = new CancellationTokenSource();
        GoalAcceptanceVerifier.CommandResult? result = null;
        Task<GoalAcceptanceVerifier.CommandResult>? run = null;
        try
        {
            run = GoalAcceptanceVerifier.RunProcessWithCaptureLimitForTestsAsync(
                LoopingArguments(), Path.GetTempPath(), TimeSpan.FromMinutes(30), CapBytes,
                GateHeartbeatRunClass.FocusedEvidence, cancellationToken: cancellation.Token);
            result = await run.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.True(result.CaptureLimited);
            Assert.False(result.TimedOut);
            Assert.Equal(CapBytes, result.CaptureLimitBytes);
            Assert.NotNull(result.ChildProcessId);
            AssertProcessExited(result.ChildProcessId!.Value);
            Assert.Equal(1, CountOccurrences(result.Output, "ACCEPTANCE_CAPTURE_LIMIT_REACHED"));
        }
        finally
        {
            await cancellation.CancelAsync();
            if (run is not null) try { await run.WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
            DeleteCaptureFiles(result);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task FullGateCommandStillTimesOutAfterCaptureLimit()
    {
        var root = CreateRoot();
        GoalAcceptanceVerifier.CommandResult? processResult = null;
        try
        {
            var verifier = new GoalAcceptanceVerifier(async (arguments, workingDirectory, _, cancellationToken) =>
            {
                processResult = await GoalAcceptanceVerifier.RunProcessWithCaptureLimitForTestsAsync(
                    arguments, workingDirectory, TimeSpan.FromSeconds(3), CapBytes,
                    GateHeartbeatRunClass.Acceptance, cancellationToken: cancellationToken);
                return processResult;
            });
            await using var owner = CreateGateOwner(root);
            var check = LoopingCheck(timeoutMinutes: 1);
            var run = await owner.RunInvocationForTestsAsync(
                verifier, owner.CreateInvocation(check.Name), check, Path.GetTempPath());

            Assert.False(run.Result.Passed);
            Assert.StartsWith("acceptance-check-timeout:", run.Result.Name, StringComparison.Ordinal);
            Assert.NotNull(processResult);
            Assert.True(processResult!.TimedOut);
            Assert.False(processResult.CaptureLimited);
            Assert.Equal(1, CountOccurrences(processResult.Output, "ACCEPTANCE_CAPTURE_LIMIT_REACHED"));
        }
        finally
        {
            DeleteCaptureFiles(processResult);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task FocusedCommandUnderCapKeepsItsOrdinaryResultName()
    {
        var root = CreateRoot();
        await using var owner = CreateFocusedOwner(root);
        var check = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "small focused command", Type = "command", Command = "pwsh",
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", "[Console]::WriteLine('ok')"],
            TimeoutMinutes = 1
        };
        try
        {
            var verifier = new GoalAcceptanceVerifier();
            var run = await verifier.RunInvocationForOwnerTestsAsync(
                owner, owner.CreateInvocation(check.Name), check, Path.GetTempPath(), owner.CancellationToken);

            Assert.True(run.Result.Passed);
            Assert.Equal(check.Name, run.Result.Name);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task FocusedEvidenceDetailNamesCaptureLimitAndCapBytes()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var manifestPath = Path.Combine(root, "config", "acceptance-manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            manifest["engine"]!["outputCaptureLimitBytes"] = CapBytes;
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            var verifier = new GoalAcceptanceVerifier((arguments, _, _, _) =>
                Task.FromResult(arguments.Length > 1 && arguments[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? new GoalAcceptanceVerifier.CommandResult(
                        -1, "ACCEPTANCE_CAPTURE_LIMIT_REACHED cap_bytes=4096",
                        CaptureLimited: true, CaptureLimitBytes: CapBytes)
                    : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.")));
            var result = await verifier.RunFocusedEvidenceAsync(
                root, null, "Infrastructure.Tests: FullyQualifiedName~GoalAcceptanceVerifierTestsFocusedCaptureLimit");

            Assert.True(result.Accepted);
            Assert.False(result.Passed);
            Assert.Contains("acceptance-check-capture-limit:", result.Summary, StringComparison.Ordinal);
            Assert.Contains("cap_bytes=4096", result.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("acceptance-check-timeout", result.Summary, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    private static string[] LoopingArguments() =>
        ["pwsh", "-NoProfile", "-NonInteractive", "-Command",
            "while ($true) { [Console]::WriteLine('xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx') }"];

    private static GoalAcceptanceVerifier.AcceptanceManifestCheck LoopingCheck(int timeoutMinutes, string? pidPath = null) => new()
    {
        Name = "unbounded output", Type = "command", Command = "pwsh",
        Arguments = pidPath is null ? LoopingArguments()[1..] :
            ["-NoProfile", "-NonInteractive", "-Command",
                $"Set-Content -LiteralPath '{pidPath.Replace("'", "''", StringComparison.Ordinal)}' -Value $PID; " +
                "while ($true) { [Console]::WriteLine('xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx') }"],
        TimeoutMinutes = timeoutMinutes
    };

    private static AcceptanceFocusedVerificationOwner CreateFocusedOwner(string root) => new(
        new AcceptanceFocusedVerificationIdentity("focused-capture-test", "goal", root, "candidate",
            Path.Combine(root, "focused-results"), null),
        new AcceptanceGateEngineSettings { OutputCaptureLimitBytes = CapBytes });

    private static AcceptanceAttemptExecutionOwner CreateGateOwner(string root) => new(
        new AcceptanceAttemptIdentity("gate-capture-test", "goal", root, "candidate", "main",
            "candidate", Path.Combine(root, "gate-results"), null, Environment.ProcessId, null),
        new AcceptanceGateEngineSettings { OutputCaptureLimitBytes = CapBytes });

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-focused-capture-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void AssertProcessExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Assert.True(process.HasExited);
        }
        catch (ArgumentException)
        {
            // The OS has already released the child process identity.
        }
    }

    private static int CountOccurrences(string text, string token) =>
        (text.Length - text.Replace(token, "", StringComparison.Ordinal).Length) / token.Length;

    private static void DeleteCaptureFiles(GoalAcceptanceVerifier.CommandResult? result)
    {
        if (result?.StdoutPath is { } stdout) try { File.Delete(stdout); } catch { }
        if (result?.StderrPath is { } stderr) try { File.Delete(stderr); } catch { }
    }
}

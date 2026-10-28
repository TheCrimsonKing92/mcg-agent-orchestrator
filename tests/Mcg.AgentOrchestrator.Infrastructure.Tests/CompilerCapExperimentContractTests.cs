using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CompilerCapExperimentContractTests
{
    // Parallel-safe: every process, event and artifact belongs to a unique fixture root.
    [Xunit.Fact]
    public void DefaultEntryPointPlansWithoutCreatingOutputsOrLaunchingBuilds()
    {
        using var fixture = new Fixture();
        var before = fixture.SourceDigest();
        var result = fixture.Invoke(run: false);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var plan = JsonDocument.Parse(result.Stdout);
        Assert.Equal("plan", plan.RootElement.GetProperty("Mode").GetString());
        Assert.Equal("synthetic-fixture", plan.RootElement.GetProperty("EvidenceSource").GetString());
        Assert.Equal(new[] { "baseline", "count-8", "count-4" },
            plan.RootElement.GetProperty("Arms").EnumerateArray().Select(a => a.GetProperty("Name").GetString()));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "experiments")));
        Assert.Equal(before, fixture.SourceDigest());
    }

    [Xunit.Fact]
    public void ExplicitRunUsesThreeSequentialColdArmsAndPreservesSourceAndSharedSentinels()
    {
        using var fixture = new Fixture();
        var before = fixture.SourceDigest();
        var result = fixture.Invoke(run: true);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        var report = fixture.Report();
        Assert.Equal("completed", report["Status"]!.GetValue<string>());
        Assert.Equal("synthetic-fixture", report["Plan"]!["EvidenceSource"]!.GetValue<string>());
        var invocations = report["Invocations"]!.AsArray();
        Assert.Equal(new[] { "baseline/setup", "baseline/build", "count-8/setup", "count-8/build", "count-4/setup", "count-4/build" },
            invocations.Select(p => p!["Arm"]!.GetValue<string>() + "/" + p["Phase"]!.GetValue<string>()));
        Assert.All(invocations, p =>
        {
            Assert.Equal("completed", p!["Status"]!.GetValue<string>());
            Assert.True(p["Result"]!["CleanupConfirmed"]!.GetValue<bool>());
            Assert.True(p["TimeoutSeconds"]!.GetValue<int>() > 0);
            Assert.False(p["Environment"]!.AsObject().ContainsKey("DOTNET_PROCESSOR_COUNT"));
            var args = p["Arguments"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
            Assert.Contains(fixture.Project, args); // One array entry despite spaces and '&' in the path.
            Assert.Contains("-p:UseSharedCompilation=false", args);
            Assert.DoesNotContain(args, a => a.Contains("RunAnalyzers=false", StringComparison.Ordinal));
            Assert.DoesNotContain(args, a => a.Contains("DOTNET_PROCESSOR_COUNT", StringComparison.Ordinal));
        });
        var arms = report["Arms"]!.AsArray();
        Assert.Equal(3, arms.Select(a => a!["OutputRoot"]!.GetValue<string>()).Distinct().Count());
        foreach (var arm in arms)
        {
            var path = arm!["OutputRoot"]!.GetValue<string>();
            Assert.StartsWith(Path.Combine(fixture.Root, "experiments"), path, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(path, "setup", "fixture-phase.json")));
            Assert.True(File.Exists(Path.Combine(path, "build", "fixture-phase.json")));
            var targets = File.ReadAllText(Path.Combine(path, "protocol.targets"));
            if (arm["Name"]!.GetValue<string>() == "baseline") Assert.DoesNotContain(";DOTNET_PROCESSOR_COUNT=", targets, StringComparison.Ordinal);
            else Assert.Contains("$(CscEnvironment);DOTNET_PROCESSOR_COUNT=", targets, StringComparison.Ordinal);
        }
        Assert.Equal(before, fixture.SourceDigest());
        Assert.Equal("shared sentinel", File.ReadAllText(Path.Combine(fixture.Root, "shared-output.txt")));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(fixture.ReportPath)!, "owner.json")));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(fixture.ReportPath)!, "summary.txt")));
    }

    [Xunit.Theory]
    [Xunit.InlineData(8.0, 110.0, 105.0, "exploratory promising")]
    [Xunit.InlineData(8.00001, 110.0, 105.0, "not justified")]
    [Xunit.InlineData(8.0, 110.00001, 105.0, "not justified")]
    [Xunit.InlineData(8.0, 110.0, 105.00001, "not justified")]
    public void UnroundedInclusiveThresholdsDecideOnlyCompleteComparisons(double peak, double wall, double cpu, string expected)
    {
        using var fixture = new Fixture();
        fixture.SetTreatment(peak, wall, cpu);
        var result = fixture.Invoke(run: true);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        var comparison = fixture.Report()["Comparisons"]![0]!;
        Assert.Equal(expected, comparison["Verdict"]!.GetValue<string>());
        Assert.True(comparison["DiagnosticsMatch"]!.GetValue<bool>());
        Assert.NotNull(comparison["Ratios"]);
    }

    [Xunit.Theory]
    [Xunit.InlineData("PopulationComplete")]
    [Xunit.InlineData("LifetimesComplete")]
    [Xunit.InlineData("CountersAvailable")]
    [Xunit.InlineData("InitialFinalReconciled")]
    [Xunit.InlineData("OutputComplete")]
    [Xunit.InlineData("DiagnosticsComplete")]
    [Xunit.InlineData("ForwardingVerified")]
    [Xunit.InlineData("HostPopulationComplete")]
    public void MissingRequiredCoverageIsInconclusive(string field)
    {
        using var fixture = new Fixture();
        fixture.Treatment["Coverage"]![field] = false;
        fixture.AssertInconclusive("missing-" + field);
        if (field is "PopulationComplete" or "LifetimesComplete" or "CountersAvailable" or "InitialFinalReconciled")
        {
            var measurements = fixture.Report()["Arms"]![1]!["Measurements"]!;
            Assert.Null(measurements["CompilerCpu"]!["Value"]);
            Assert.False(measurements["CompilerCpu"]!["Available"]!.GetValue<bool>());
            Assert.NotEmpty(measurements["CompilerCpu"]!["Reason"]!.GetValue<string>());
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("no-work", "no-actual-compiler-execution")]
    [Xunit.InlineData("zero-work", "zero-compiler-work")]
    [Xunit.InlineData("few-windows", "fewer-than-10-active-windows")]
    [Xunit.InlineData("sample-gap", "compiler-active-gap")]
    [Xunit.InlineData("counter-null", "counter-unavailable")]
    [Xunit.InlineData("counter-regression", "counter-regression-or-gap")]
    [Xunit.InlineData("pid-reuse", "pid-identity-ambiguity")]
    [Xunit.InlineData("unknown-short-compiler", "compiler-population-mismatch")]
    [Xunit.InlineData("missing-final", "incomplete-lifetime-accounting")]
    [Xunit.InlineData("missing-initial", "initial-final-counter-accounting-gap")]
    [Xunit.InlineData("server", "shared-or-unaccounted-compiler")]
    [Xunit.InlineData("compiler-identity", "compiler-identity-mismatch")]
    [Xunit.InlineData("host-fact", "host-fact-unavailable-PowerPlan")]
    [Xunit.InlineData("host-counter", "host-counter-unavailable")]
    [Xunit.InlineData("host-mismatch", "non-equivalent-Host")]
    [Xunit.InlineData("dependencies", "non-equivalent-DependencyIdentity")]
    [Xunit.InlineData("settings", "non-equivalent-EffectiveSettings")]
    [Xunit.InlineData("identity-missing", "identity-evidence-missing-WorkloadIdentity")]
    [Xunit.InlineData("background-high", "background-cpu-above-10-percent")]
    [Xunit.InlineData("background-difference", "background-arm-difference-above-5-points")]
    public void RawInvalidEvidenceCannotBecomeAPerformanceVerdict(string scenario, string reason)
    {
        using var fixture = new Fixture();
        var observation = fixture.Treatment;
        var process = observation["Processes"]![0]!;
        var windows = observation["Windows"]!.AsArray();
        switch (scenario)
        {
            case "no-work": observation["Processes"] = new JsonArray(); observation["CompilerExecutions"] = 0; observation["Windows"] = new JsonArray(); break;
            case "zero-work":
                process["FinalCpuSeconds"] = 0;
                foreach (var window in windows) { window!["ProcessCounters"]![0]!["StartCpuSeconds"] = 0; window["ProcessCounters"]![0]!["EndCpuSeconds"] = 0; }
                break;
            case "few-windows": while (windows.Count > 9) windows.RemoveAt(windows.Count - 1); break;
            case "sample-gap": windows[1]!["EndSeconds"] = 0.751; break;
            case "counter-null": windows[0]!["ProcessCounters"]![0]!["EndCpuSeconds"] = null; break;
            case "counter-regression": windows[0]!["ProcessCounters"]![0]!["EndCpuSeconds"] = -1; break;
            case "pid-reuse": windows[0]!["ProcessCounters"]![0]!["StartUtc"] = "different-start"; break;
            case "unknown-short-compiler": observation["CompilerExecutions"] = 2; break;
            case "missing-final": process["FinalCpuSeconds"] = null; process["ExitAccounted"] = false; break;
            case "missing-initial": windows[0]!["ProcessCounters"]![0]!["StartCpuSeconds"] = 0.1; break;
            case "server": observation["Coverage"]!["SharedCompiler"] = true; break;
            case "compiler-identity": process["CompilerSha256"] = "different"; break;
            case "host-fact": observation["Host"]!["PowerPlan"] = null; break;
            case "host-counter": windows[0]!["HostCpuSeconds"] = null; break;
            case "host-mismatch": observation["Host"]!["Affinity"] = "changed"; break;
            case "dependencies": observation["DependencyIdentity"] = "changed"; break;
            case "settings": observation["EffectiveSettings"] = "changed"; break;
            case "identity-missing": observation["WorkloadIdentity"] = null; break;
            case "background-high": Fixture.SetBackground(observation, 10.0001); break;
            case "background-difference": Fixture.SetBackground(observation, 6.0001); break;
            default: throw new InvalidOperationException("Unknown negative control.");
        }
        fixture.AssertInconclusive(reason);
        if (scenario == "counter-null")
        {
            var window = fixture.Report()["Arms"]![1]!["Measurements"]!["ActiveWindows"]![0]!;
            Assert.False(window["Valid"]!.GetValue<bool>());
            Assert.Null(window["CoreEquivalents"]);
            Assert.NotEmpty(window["Reason"]!.GetValue<string>());
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("Id")]
    [Xunit.InlineData("Severity")]
    [Xunit.InlineData("Project")]
    [Xunit.InlineData("File")]
    [Xunit.InlineData("Line")]
    [Xunit.InlineData("Column")]
    [Xunit.InlineData("EndLine")]
    [Xunit.InlineData("EndColumn")]
    [Xunit.InlineData("duplicates")]
    public void DiagnosticMultisetPreservesIdentityLocationAndDuplicateCounts(string field)
    {
        using var fixture = new Fixture();
        var diagnostics = fixture.Treatment["Diagnostics"]!.AsArray();
        if (field == "duplicates") diagnostics.Add(diagnostics[0]!.DeepClone());
        else if (field.Contains("Line", StringComparison.Ordinal) || field.Contains("Column", StringComparison.Ordinal)) diagnostics[0]![field] = 99;
        else diagnostics[0]![field] = "changed";
        fixture.AssertInconclusive("diagnostic-multiset-mismatch");
        Assert.False(fixture.Report()["Comparisons"]![0]!["DiagnosticsMatch"]!.GetValue<bool>());
    }

    [Xunit.Fact]
    public void EquivalentOwnedRootsNormalizeWhileDistinctLocationsRemainDifferent()
    {
        using var fixture = new Fixture();
        foreach (var (observation, arm) in new[] { (fixture.Baseline, "baseline"), (fixture.Treatment, "count-8") })
        {
            var diagnostic = observation["Diagnostics"]![0]!;
            diagnostic["Project"] = fixture.Project;
            diagnostic["File"] = Path.Combine(fixture.Root, "experiments", "fixture-run", arm, "outputs", "generated.cs");
        }
        var result = fixture.Invoke(run: true);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        var comparison = fixture.Report()["Comparisons"]![0]!;
        Assert.True(comparison["DiagnosticsMatch"]!.GetValue<bool>());
        Assert.Equal("exploratory promising", comparison["Verdict"]!.GetValue<string>());
        Assert.Contains("<owned>/outputs/generated.cs", fixture.Report()["Arms"]![0]!["Measurements"]!["Diagnostics"]![0]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("equivalent", true)]
    [Xunit.InlineData("different-file", false)]
    [Xunit.InlineData("different-project", false)]
    [Xunit.InlineData("outside-root", false)]
    [Xunit.InlineData("distinct-outside-roots", false)]
    public void RelativeAndAbsoluteDiagnosticsPreserveLocationIdentity(string scenario, bool matches)
    {
        using var fixture = new Fixture();
        var repository = Path.Combine(fixture.Root, "source");
        var baseline = fixture.Baseline["Diagnostics"]![0]!;
        var treatment = fixture.Treatment["Diagnostics"]![0]!;
        treatment["Project"] = Path.Combine(repository, "src", ".", "App.csproj");
        treatment["File"] = Path.Combine(repository, "src", "nested", "..", "Program.cs");
        if (scenario == "different-file") treatment["File"] = Path.Combine(repository, "src", "Other.cs");
        if (scenario == "different-project") treatment["Project"] = Path.Combine(repository, "src", "Other.csproj");
        if (scenario is "outside-root" or "distinct-outside-roots")
            treatment["File"] = Path.Combine(fixture.Root, "outside-one", "src", "Program.cs");
        if (scenario == "distinct-outside-roots")
            baseline["File"] = Path.Combine(fixture.Root, "outside-two", "src", "Program.cs");

        var result = fixture.Invoke(run: true);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        var report = fixture.Report();
        var comparison = report["Comparisons"]![0]!;
        Assert.Equal(matches, comparison["DiagnosticsMatch"]!.GetValue<bool>());
        Assert.Equal(matches ? "exploratory promising" : "inconclusive", comparison["Verdict"]!.GetValue<string>());
        if (matches)
        {
            Assert.Equal(report["Arms"]![0]!["Measurements"]!["Diagnostics"]!.ToJsonString(),
                report["Arms"]![1]!["Measurements"]!["Diagnostics"]!.ToJsonString());
            Assert.Contains("<repo>/src/Program.cs", report["Arms"]![0]!["Measurements"]!["Diagnostics"]![0]!.GetValue<string>(), StringComparison.Ordinal);
        }
        else Assert.Contains("diagnostic-multiset-mismatch", comparison["Reasons"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(23)]
    public void NativeProcessForwardsArgumentsDrainsBothPipesAndRetainsExit(int exitCode)
    {
        using var fixture = new Fixture();
        fixture.EnableNativeProcess(exitCode);
        var before = fixture.SourceDigest();
        var result = fixture.Invoke(run: true);
        var report = fixture.Report();
        var evidence = result.Stdout + result.Stderr + report.ToJsonString();
        // Put the native completion reason before the full report, which can be truncated
        // by the test host. A launch/drain failure must identify the failing phase.
        foreach (var invocation in report["Invocations"]!.AsArray())
        {
            var phase = invocation!["Result"]!;
            Assert.True(phase?["Status"]?.GetValue<string>() == "completed",
                $"Native {invocation["Arm"]}/{invocation["Phase"]}: {phase?.ToJsonString()}\n{evidence}");
        }
        Assert.True(result.ExitCode == (exitCode == 0 ? 0 : 1), evidence);
        Assert.Equal(exitCode == 0 ? "completed" : "terminated", report["Status"]!.GetValue<string>());
        var invocations = report["Invocations"]!.AsArray();
        Assert.Equal(exitCode == 0 ? 6 : 1, invocations.Count);
        foreach (var invocation in invocations)
        {
            var phase = invocation!["Result"]!;
            Assert.True(phase is not null, evidence);
            Assert.Equal("completed", phase["Status"]!.GetValue<string>());
            Assert.Equal(exitCode, phase["ExitCode"]!.GetValue<int>());
            Assert.True(phase["CleanupConfirmed"]!.GetValue<bool>());
            Assert.True(phase["OutputComplete"]!.GetValue<bool>());
            var root = Path.Combine(Path.GetDirectoryName(fixture.ReportPath)!,
                invocation["Arm"]!.GetValue<string>(), invocation["Phase"]!.GetValue<string>());
            var receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "child.json")))!;
            Assert.True(receipt["InJob"]!.GetValue<bool>());
            Assert.Equal("job-assigned", receipt["Gate"]!.GetValue<string>());
            Assert.Equal(invocation["Arguments"]!.ToJsonString(), receipt["Arguments"]!.ToJsonString());
            Assert.Equal(invocation["Environment"]!["DOTNET_CLI_HOME"]!.GetValue<string>(), receipt["CliHome"]!.GetValue<string>());
            Assert.True(string.IsNullOrEmpty(receipt["ProcessorCount"]?.GetValue<string>()));
            Assert.Equal(phase["Process"]!.ToJsonString(), receipt["Process"]!.ToJsonString());
            var request = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "request.json")))!;
            Assert.Equal(WorkerShell.Executable, request["Executable"]!.GetValue<string>(), ignoreCase: true);
            Assert.Equal(invocation["Arguments"]!.AsArray().Select(a => a!.GetValue<string>()),
                request["Arguments"]!.AsArray().Skip(6).Select(a => a!.GetValue<string>()));
            Assert.Equal(new string('O', 131072) + "stdout-tail", File.ReadAllText(Path.Combine(root, "build.stdout.log")));
            Assert.Equal(new string('E', 131072) + "stderr-tail", File.ReadAllText(Path.Combine(root, "build.stderr.log")));
        }
        Assert.Equal(before, fixture.SourceDigest());
        Assert.Equal("shared sentinel", File.ReadAllText(Path.Combine(fixture.Root, "shared-output.txt")));
        Assert.All(report["Comparisons"]!.AsArray(), c => Assert.Equal("inconclusive", c!["Verdict"]!.GetValue<string>()));
        if (exitCode != 0)
        {
            Assert.Equal("skipped", report["Arms"]![1]!["Status"]!.GetValue<string>());
            Assert.Equal("skipped", report["Arms"]![2]!["Status"]!.GetValue<string>());
        }
    }

    [Xunit.Fact]
    public async Task NativeDeadlineTerminatesOwnedChildAndDescendantWithoutRetry()
    {
        using var fixture = new Fixture();
        var deadlineName = "Local\\compiler-cap-" + Guid.NewGuid().ToString("N");
        var readyName = "Local\\compiler-cap-ready-" + Guid.NewGuid().ToString("N");
        using var deadline = new EventWaitHandle(false, EventResetMode.ManualReset, deadlineName);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        using var invocationComplete = new ManualResetEvent(false);
        fixture.EnableNativeProcess(0, deadlineName, readyName);
        var invocation = Task.Run(() =>
        {
            try { return fixture.Invoke(run: true); }
            finally { invocationComplete.Set(); }
        });
        Process? child = null;
        Process? descendant = null;
        try
        {
            var signal = WaitHandle.WaitAny([ready, invocationComplete], TestHangGuard.HangSafetyBound);
            if (signal == 1)
            {
                var failed = await invocation;
                Assert.Fail("Native fixture exited before its ready receipt: " + failed.Stdout + failed.Stderr + fixture.Report().ToJsonString());
            }
            Assert.True(signal == 0, "Native fixture did not publish its child/descendant ready receipt.");
            var phaseRoot = Path.Combine(fixture.Root, "experiments", "fixture-run", "baseline", "setup");
            var receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(phaseRoot, "child.json")))!;
            Assert.True(receipt["InJob"]!.GetValue<bool>());
            child = RetainOwnedProcess(receipt["Process"]!);
            descendant = RetainOwnedProcess(receipt["Descendant"]!);
            Assert.False(child.HasExited);
            Assert.False(descendant.HasExited);
            deadline.Set(); // Controlled deadline; never rely on a short wall-clock timeout.
            var result = await invocation;
            Assert.NotEqual(0, result.ExitCode);
            var report = fixture.Report();
            Assert.Equal("terminated", report["Status"]!.GetValue<string>());
            var phase = Assert.Single(report["Invocations"]!.AsArray())!["Result"]!;
            Assert.Equal("timeout", phase["Status"]!.GetValue<string>());
            Assert.Null(phase["ExitCode"]);
            Assert.True(phase["CleanupConfirmed"]!.GetValue<bool>());
            Assert.True(child.WaitForExit((int)TestHangGuard.Bound.TotalMilliseconds), "Owned child did not exit after job cleanup.");
            Assert.True(descendant.WaitForExit((int)TestHangGuard.Bound.TotalMilliseconds), "Owned descendant did not exit after job cleanup.");
            Assert.True(File.Exists(Path.Combine(phaseRoot, "completion.json")));
            Assert.True(File.Exists(Path.Combine(phaseRoot, "request.json")));
            Assert.Equal("skipped", report["Arms"]![1]!["Status"]!.GetValue<string>());
            Assert.Equal("skipped", report["Arms"]![2]!["Status"]!.GetValue<string>());
        }
        finally
        {
            deadline.Set();
            await invocation;
            child?.Dispose();
            descendant?.Dispose();
        }
    }

    private static Process RetainOwnedProcess(JsonNode identity)
    {
        var process = Process.GetProcessById(identity["Pid"]!.GetValue<int>());
        _ = process.Handle; // Retain the OS handle before deadline/cleanup; never re-query a recycled PID.
        Assert.Equal(identity["StartUtc"]!.GetValue<string>(), process.StartTime.ToUniversalTime().ToString("O"));
        Assert.Equal(identity["Image"]!.GetValue<string>(), process.MainModule!.FileName);
        return process;
    }

    [Xunit.Theory]
    [Xunit.InlineData(0, "timeout", 0, true, true)]
    [Xunit.InlineData(0, "cancelled", 0, true, true)]
    [Xunit.InlineData(0, "completed", 1, true, true)]
    [Xunit.InlineData(1, "timeout", 0, true, true)]
    [Xunit.InlineData(1, "cancelled", 0, true, true)]
    [Xunit.InlineData(1, "completed", 1, true, true)]
    [Xunit.InlineData(1, "completed", 0, false, true)]
    [Xunit.InlineData(1, "completed", 0, true, false)]
    public void TerminalPhaseFailureRetainsEvidenceSkipsLaterArmsAndNeverRetries(int phaseIndex, string status, int exit, bool cleanup, bool output)
    {
        using var fixture = new Fixture();
        var before = fixture.SourceDigest();
        var phase = fixture.Data["Phases"]![phaseIndex]!;
        phase["Status"] = status;
        phase["ExitCode"] = exit;
        phase["CleanupConfirmed"] = cleanup;
        phase["OutputComplete"] = output;
        var result = fixture.Invoke(run: true);
        Assert.NotEqual(0, result.ExitCode);
        var report = fixture.Report();
        Assert.Equal("terminated", report["Status"]!.GetValue<string>());
        Assert.Equal(phaseIndex + 1, report["Invocations"]!.AsArray().Count);
        Assert.Equal("failed", report["Arms"]![0]!["Status"]!.GetValue<string>());
        Assert.Equal("skipped", report["Arms"]![1]!["Status"]!.GetValue<string>());
        Assert.Equal("skipped", report["Arms"]![2]!["Status"]!.GetValue<string>());
        Assert.All(report["Comparisons"]!.AsArray(), c => Assert.Equal("inconclusive", c!["Verdict"]!.GetValue<string>()));
        Assert.True(File.Exists(Path.Combine(report["Arms"]![0]!["OutputRoot"]!.GetValue<string>(), phaseIndex == 0 ? "setup" : "build", "fixture-phase.json")));
        Assert.Equal(before, fixture.SourceDigest());
    }

    [Xunit.Theory]
    [Xunit.InlineData("candidate")]
    [Xunit.InlineData("sdk")]
    [Xunit.InlineData("input")]
    public void MismatchedPhaseIdentityTerminatesBeforeInvokingThatPhase(string field)
    {
        using var fixture = new Fixture();
        fixture.Data["Phases"]![2]!["Identity"] = new JsonObject { [field] = "mismatched" };
        var result = fixture.Invoke(run: true);
        Assert.NotEqual(0, result.ExitCode);
        var report = fixture.Report();
        Assert.Equal("terminated", report["Status"]!.GetValue<string>());
        Assert.Contains("candidate-or-sdk-or-input-mismatch", report["Reasons"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(3, report["Invocations"]!.AsArray().Count);
        Assert.Null(report["Invocations"]![2]!["Result"]);
        Assert.Equal("skipped", report["Arms"]![2]!["Status"]!.GetValue<string>());
    }

    [Xunit.Fact]
    public void BackgroundAndSamplingBoundaryValuesAreInclusiveAndNativeResultsCannotBeForged()
    {
        using var fixture = new Fixture();
        Fixture.SetBackground(fixture.Baseline, 5);
        Fixture.SetBackground(fixture.Treatment, 10);
        var result = fixture.Invoke(run: true);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Equal("exploratory promising", fixture.Report()["Comparisons"]![0]!["Verdict"]!.GetValue<string>());
        Assert.Equal("synthetic-fixture", fixture.Report()["Plan"]!["EvidenceSource"]!.GetValue<string>());
        Assert.True(fixture.Report()["Arms"]![0]!["Measurements"]!["ActiveWindows"]!.AsArray().Count >= 10);
    }

    [Xunit.Fact]
    public void FixtureWithoutOwnershipMarkerFailsWithoutNativeFallbackOrOutputs()
    {
        using var fixture = new Fixture();
        File.Delete(Path.Combine(fixture.Root, ".compiler-cap-test-fixture"));
        var result = fixture.Invoke(run: true);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("fixture-ownership-required", result.Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "experiments")));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "compiler cap & fixture " + Guid.NewGuid().ToString("N"));
        public string Project => Path.Combine(Root, "source", "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj");
        public JsonObject Data { get; }
        public JsonNode Baseline => Data["Phases"]![1]!["Observation"]!;
        public JsonNode Treatment => Data["Phases"]![3]!["Observation"]!;
        public string ReportPath => Assert.Single(Directory.GetFiles(Path.Combine(Root, "experiments"), "report.json", SearchOption.AllDirectories));

        public Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Project)!);
            File.WriteAllText(Project, "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><ProjectReference Include=\"../Core.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(Root, "source", "Directory.Build.props"), "<Project><PropertyGroup><AnalysisLevel>latest</AnalysisLevel></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(Root, ".compiler-cap-test-fixture"), "owned");
            File.WriteAllText(Path.Combine(Root, "shared-output.txt"), "shared sentinel");
            Data = new JsonObject
            {
                ["Contract"] = "compiler-cap-fixture-v1",
                ["Sdk"] = new JsonObject { ["Version"] = "synthetic-sdk", ["Executable"] = "MUST-NOT-LAUNCH", ["Compiler"] = "synthetic-csc.dll", ["CompilerSha256"] = "csc-hash", ["CscEnvironmentForwarded"] = true },
                ["Phases"] = new JsonArray(Phase(), Phase(Observation(10, 100)), Phase(), Phase(Observation(8, 105)), Phase(), Phase(Observation(8, 105)))
            };
        }

        public TestChildProcessCapture.Result Invoke(bool run)
        {
            var input = Path.Combine(Root, "adapter.json");
            File.WriteAllText(input, Data.ToJsonString());
            var repository = VerifiedRepositoryRoot.Find();
            var info = new ProcessStartInfo(WorkerShell.Executable)
            {
                WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(repository, "scripts", "Measure-CompilerCap.ps1"), "-FixtureFile", input }) info.ArgumentList.Add(arg);
            if (run) info.ArgumentList.Add("-Run");
            var path = Environment.GetEnvironmentVariable("PATH");
            var systemRoot = Environment.GetEnvironmentVariable("SYSTEMROOT");
            info.Environment.Clear();
            if (path is not null) info.Environment["PATH"] = path;
            if (systemRoot is not null) { info.Environment["SYSTEMROOT"] = systemRoot; info.Environment["WINDIR"] = systemRoot; }
            info.Environment["TEMP"] = Path.GetTempPath();
            info.Environment["TMP"] = Path.GetTempPath();
            return TestChildProcessCapture.Run(info, hangBound: Data.ContainsKey("NativeProcess") ? TestHangGuard.HangSafetyBound : TestHangGuard.Bound);
        }

        public void EnableNativeProcess(int exitCode, string deadlineName = "", string readyName = "")
        {
            var script = Path.Combine(Root, "bounded process & adapter.ps1");
            File.WriteAllText(script, """
                Set-StrictMode -Version Latest
                $ErrorActionPreference = 'Stop'
                # -File binds colon-bearing MSBuild switches in $args (for example
                # -target:Restore becomes two entries). Observe the actual native argv
                # instead, without PowerShell parameter binding changing its shape.
                $nativeArguments = [Environment]::GetCommandLineArgs()
                $scriptIndex = [Array]::IndexOf($nativeArguments, $PSCommandPath)
                if ($scriptIndex -lt 0 -or $nativeArguments.Length -le $scriptIndex + 3) { throw 'native-argument-prefix-missing' }
                $EvidenceRoot = $nativeArguments[$scriptIndex + 1]
                $DeadlineEventName = $nativeArguments[$scriptIndex + 2]
                $forwardedArguments = @($nativeArguments[($scriptIndex + 3)..($nativeArguments.Length - 1)])
                Add-Type -TypeDefinition @'
                using System;
                using System.Runtime.InteropServices;
                public static class FixtureJob {
                    [DllImport("kernel32.dll", SetLastError=true)]
                    public static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
                }
                '@
                $self = Get-Process -Id $PID
                $inJob = $false
                if (-not [FixtureJob]::IsProcessInJob($self.Handle, [IntPtr]::Zero, [ref]$inJob)) { throw 'job-query-failed' }
                $receipt = [ordered]@{
                    InJob = $inJob; Gate = [IO.File]::ReadAllText((Join-Path $EvidenceRoot 'start-owned'))
                    Arguments = $forwardedArguments; CliHome = $env:DOTNET_CLI_HOME; ProcessorCount = $env:DOTNET_PROCESSOR_COUNT
                    Process = [ordered]@{ Pid = $PID; StartUtc = $self.StartTime.ToUniversalTime().ToString('O'); Image = $self.Path }
                    Descendant = $null
                }
                $arm = Split-Path -Parent $EvidenceRoot
                [void][IO.Directory]::CreateDirectory((Join-Path $arm 'outputs'))
                [void][IO.Directory]::CreateDirectory((Join-Path $arm 'packages'))
                if ($DeadlineEventName) {
                    $info = [Diagnostics.ProcessStartInfo]::new($self.Path)
                    $info.UseShellExecute = $false
                    $info.CreateNoWindow = $true
                    foreach ($arg in @('-NoProfile','-NonInteractive','-Command', '[Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset).WaitOne(120000)')) { $info.ArgumentList.Add($arg) }
                    $descendant = [Diagnostics.Process]::Start($info)
                    $receipt.Descendant = [ordered]@{ Pid = $descendant.Id; StartUtc = $descendant.StartTime.ToUniversalTime().ToString('O'); Image = $descendant.MainModule.FileName }
                }
                [IO.File]::WriteAllText((Join-Path $EvidenceRoot 'child.json'), ($receipt | ConvertTo-Json -Depth 10))
                if ($DeadlineEventName) {
                    $ready = [Threading.EventWaitHandle]::OpenExisting('__READY__')
                    [void]$ready.Set()
                    $ready.Dispose()
                    # Only owned job termination should end this wait; timeout is a hang guard.
                    [void][Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset).WaitOne(120000)
                    throw 'owned-fixture-was-not-terminated'
                }
                [Console]::Out.Write(('O' * 131072) + 'stdout-tail')
                [Console]::Error.Write(('E' * 131072) + 'stderr-tail')
                exit __EXIT__
                """.Replace("__READY__", readyName, StringComparison.Ordinal)
                   .Replace("__EXIT__", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
            Data["Sdk"]!["Executable"] = WorkerShell.Executable;
            Data["NativeProcess"] = new JsonObject
            {
                ["Contract"] = "compiler-cap-native-process-fixture-v1", ["Script"] = script,
                ["DeadlineAfterReady"] = deadlineName.Length != 0, ["DeadlineEventName"] = deadlineName
            };
        }

        public JsonNode Report() => JsonNode.Parse(File.ReadAllText(ReportPath))!;

        public void SetTreatment(double peak, double wall, double cpu)
        {
            Data["Phases"]![3]!["ElapsedSeconds"] = wall;
            Data["Phases"]![3]!["Observation"] = Observation(peak, cpu);
        }

        public void AssertInconclusive(string reason)
        {
            var result = Invoke(run: true);
            Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
            var comparison = Report()["Comparisons"]![0]!;
            Assert.Equal("inconclusive", comparison["Verdict"]!.GetValue<string>());
            Assert.Contains(reason, comparison["Reasons"]!.ToJsonString(), StringComparison.Ordinal);
            Assert.Null(comparison["Ratios"]);
        }

        public string SourceDigest() => string.Join("|", Directory.GetFiles(Path.Combine(Root, "source"), "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(p => p + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));

        public static void SetBackground(JsonNode observation, double percent)
        {
            foreach (var window in observation["Windows"]!.AsArray())
            {
                var duration = window!["EndSeconds"]!.GetValue<double>() - window["StartSeconds"]!.GetValue<double>();
                window["HostCpuSeconds"] = window["ExperimentCpuSeconds"]!.GetValue<double>() + percent / 100 * 40 * duration;
            }
        }

        private static JsonObject Phase(JsonObject? observation = null) => new()
        {
            ["Status"] = "completed", ["ExitCode"] = 0, ["CleanupConfirmed"] = true, ["OutputComplete"] = true,
            ["ElapsedSeconds"] = observation is null ? 999 : 100, ["Observation"] = observation
        };

        private static JsonObject Observation(double peak, double totalCpu)
        {
            // Twenty seconds sampled at 250 ms. The final window supplies a separately controlled
            // peak; the other 79 windows reconcile to the specified total lifetime CPU.
            var windows = new JsonArray();
            var cpu = 0.0;
            var ordinaryStep = Math.Floor((totalCpu - peak * 0.25) / 79 * 1024) / 1024;
            for (var i = 0; i < 80; i++)
            {
                var next = i switch { 79 => totalCpu, 78 => totalCpu - peak * 0.25, _ => ordinaryStep * (i + 1) };
                windows.Add(new JsonObject
                {
                    ["StartSeconds"] = i * 0.25, ["EndSeconds"] = (i + 1) * 0.25,
                    ["HostCpuSeconds"] = next - cpu + 0.08, ["ExperimentCpuSeconds"] = next - cpu,
                    ["ProcessCounters"] = new JsonArray(new JsonObject
                    {
                        ["Pid"] = 123, ["StartUtc"] = "2030-01-01T00:00:00Z", ["Image"] = "MUST-NOT-LAUNCH",
                        ["StartCpuSeconds"] = cpu, ["EndCpuSeconds"] = next
                    })
                });
                cpu = next;
            }
            // Keep total and peak independent: the baseline peak must dominate its mean.
            return new JsonObject
            {
                ["Coverage"] = new JsonObject
                {
                    ["PopulationComplete"] = true, ["LifetimesComplete"] = true, ["CountersAvailable"] = true,
                    ["InitialFinalReconciled"] = true, ["OutputComplete"] = true, ["DiagnosticsComplete"] = true,
                    ["ForwardingVerified"] = true, ["HostPopulationComplete"] = true, ["SharedCompiler"] = false
                },
                ["CompilerExecutions"] = 1,
                ["Processes"] = new JsonArray(new JsonObject
                {
                    ["Pid"] = 123, ["StartUtc"] = "2030-01-01T00:00:00Z", ["Image"] = "MUST-NOT-LAUNCH", ["CompilerAssembly"] = "synthetic-csc.dll", ["CompilerSha256"] = "csc-hash",
                    ["StartSeconds"] = 0, ["ExitSeconds"] = 20, ["StartAccounted"] = true, ["ExitAccounted"] = true, ["FinalCpuSeconds"] = totalCpu
                }),
                ["Windows"] = windows,
                ["Diagnostics"] = new JsonArray(new JsonObject
                {
                    ["Id"] = "CA1234", ["Severity"] = "warning", ["Project"] = "src/App.csproj", ["File"] = "src/Program.cs",
                    ["Line"] = 1, ["Column"] = 2, ["EndLine"] = 1, ["EndColumn"] = 3
                }),
                ["Host"] = new JsonObject
                {
                    ["Hardware"] = "fixture-hardware", ["LogicalProcessors"] = 40, ["OS"] = "fixture-os", ["Priority"] = "Normal",
                    ["Affinity"] = "all", ["DefaultCpuSet"] = "all", ["PowerPlan"] = "fixture-plan", ["Throttling"] = "unchanged"
                },
                ["WorkloadIdentity"] = "fixture-workload", ["DependencyIdentity"] = "fixture-dependencies", ["EffectiveSettings"] = "fixture-quality-settings"
            };
        }

        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), resolved, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(resolved, recursive: true);
        }
    }
}

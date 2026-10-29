using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: every fixture owns its output, published cache, and child environment.
// Only the resolver/parity contracts spawn PowerShell; supervisor children use a fake host.
public sealed class ConductorRunClosureCheckTests
{
    private const string AppLeaf = "Mcg.AgentOrchestrator.App.dll";
    private const string DeletedFile = "de/x.resources.dll";

    [Xunit.Fact]
    public void Intact_sealed_folder_is_reported_intact()
    {
        using var fixture = new PublishedFixture();

        var verdict = fixture.Check();

        Assert.True(verdict.Intact, verdict.ToString());
        Assert.Equal(fixture.ExpectedCount, verdict.Expected);
        Assert.Equal(fixture.ExpectedCount, verdict.Actual);
        Assert.Equal("intact", verdict.Reason);
        Assert.Equal("none", verdict.FirstMissing);
        Assert.Equal(fixture.Inventory.Order(StringComparer.Ordinal),
            verdict.VerifiedInventory!.Order(StringComparer.Ordinal));
    }

    [Xunit.Fact]
    public void Deleting_one_file_reports_damaged_with_counts_and_missing_name()
    {
        using var fixture = new PublishedFixture();
        fixture.Delete(DeletedFile);

        var verdict = fixture.Check();

        Assert.False(verdict.Intact);
        Assert.Equal(fixture.RunDirectory, verdict.Folder);
        Assert.Equal(fixture.ExpectedCount, verdict.Expected);
        Assert.Equal(fixture.ExpectedCount - 1, verdict.Actual);
        Assert.Equal(DeletedFile, verdict.FirstMissing);
        Assert.Equal("files-missing", verdict.Reason);
    }

    [Xunit.Theory]
    [Xunit.InlineData("marker-missing")]
    [Xunit.InlineData("marker-malformed")]
    [Xunit.InlineData("marker-unsupported")]
    public void Unusable_marker_is_unverifiable_with_available_actual_count(string reason)
    {
        using var fixture = new PublishedFixture();
        if (reason == "marker-missing") File.Delete(fixture.MarkerPath);
        else File.WriteAllText(fixture.MarkerPath, reason == "marker-malformed"
            ? "{invalid" : "{\"schemaVersion\":99,\"digest\":\"unsupported\",\"fileCount\":10}");

        var verdict = fixture.Check();

        Assert.False(verdict.Intact);
        Assert.Null(verdict.Expected);
        Assert.Equal(fixture.ExpectedCount, verdict.Actual);
        Assert.Equal("unavailable", verdict.FirstMissing);
        Assert.Equal(reason, verdict.Reason);
    }

    [Xunit.Theory]
    [Xunit.InlineData("altered", 0)]
    [Xunit.InlineData("extra", 1)]
    [Xunit.InlineData("nested-marker", 1)]
    public void Changed_payload_is_damaged_without_claiming_a_missing_file(string change, int extra)
    {
        using var fixture = new PublishedFixture();
        var relative = change switch
        {
            "altered" => "a-b.dll",
            "nested-marker" => "de/.mcg-run-closure.json",
            _ => "extra.dll"
        };
        File.WriteAllText(Path.Combine(fixture.RunDirectory, relative), "changed");

        var verdict = fixture.Check();

        Assert.False(verdict.Intact);
        Assert.Equal(fixture.ExpectedCount, verdict.Expected);
        Assert.Equal(fixture.ExpectedCount + extra, verdict.Actual);
        Assert.Equal("none", verdict.FirstMissing);
        Assert.Equal("digest-mismatch", verdict.Reason);
    }

    [Xunit.Fact]
    public void Missing_inventory_reports_unavailable_without_guessing_a_file()
    {
        using var fixture = new PublishedFixture();
        fixture.Delete(DeletedFile);

        var verdict = ConductorRunClosureCheck.Verify(fixture.RunDirectory, AppLeaf);

        Assert.False(verdict.Intact);
        Assert.Equal(fixture.ExpectedCount, verdict.Expected);
        Assert.Equal(fixture.ExpectedCount - 1, verdict.Actual);
        Assert.Equal("unavailable", verdict.FirstMissing);
        Assert.Contains("inventory-unavailable", verdict.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Unreadable_file_is_ineligible_for_launch()
    {
        using var fixture = new PublishedFixture();
        using var locked = File.Open(Path.Combine(fixture.RunDirectory, DeletedFile),
            FileMode.Open, FileAccess.Read, FileShare.None);

        var verdict = fixture.Check();

        Assert.False(verdict.Intact);
        Assert.Equal(fixture.ExpectedCount, verdict.Actual);
        Assert.Equal("enumeration-failed", verdict.Reason);
    }

    [Xunit.Theory]
    [Xunit.InlineData("intact")]
    [Xunit.InlineData("deleted-file")]
    [Xunit.InlineData("app-missing")]
    [Xunit.InlineData("sqlite-missing")]
    public void Check_agrees_with_Test_PublishedRunDirectory(string scenario)
    {
        using var fixture = new PublishedFixture();
        if (scenario != "intact") fixture.Delete(scenario switch
        {
            "app-missing" => AppLeaf,
            "sqlite-missing" => "runtimes/win-x64/native/e_sqlite3.dll",
            _ => DeletedFile
        });

        var scriptVerdict = fixture.ScriptVerdict();
        var verdict = fixture.Check();

        Assert.Equal(scenario == "intact", scriptVerdict);
        Assert.Equal(scriptVerdict, verdict.Intact);
    }

    [Xunit.Fact]
    public async Task Damaged_recorded_run_folder_escalates_without_launching_when_restage_is_unavailable()
    {
        using var fixture = new PublishedFixture();
        fixture.Delete(DeletedFile);
        var probe = new SupervisorProbe(fixture);

        Assert.Equal(1, await probe.RunAsync());

        Assert.Empty(probe.Host.Requests);
        var failed = Assert.Single(probe.Events.Where(IsFailedCheck));
        AssertDiagnostics(failed, fixture, "unavailable");
        var escalation = Assert.Single(probe.Events.Where(IsClosureEscalation));
        AssertDiagnostics(escalation, fixture, "unavailable");
        Assert.Contains("recovery=staging-unavailable", escalation, StringComparison.Ordinal);
        Assert.Single(probe.Attention);
    }

    [Xunit.Fact]
    public async Task Damaged_recorded_run_folder_launches_from_fresh_validated_folder()
    {
        using var fixture = new PublishedFixture();
        using var fresh = new PublishedFixture();
        fixture.Delete(DeletedFile);
        var probe = new SupervisorProbe(fixture);
        var stageCalls = 0;

        Assert.Equal(0, await probe.RunAsync(_ => { stageCalls++; return fresh.Successor(); }));

        Assert.Equal(1, stageCalls);
        var launched = Assert.Single(probe.Host.Requests);
        Assert.Equal(new[] { "dotnet-test", Path.Combine(fresh.RunDirectory, AppLeaf) }, launched.CommandPrefix);
        Assert.Contains("--continuity-child", launched.Arguments);
        Assert.Contains("--project=closure-test", launched.Arguments);
        Assert.Contains("--tenant=closure-tenant", launched.Arguments);
        AssertDiagnostics(Assert.Single(probe.Events.Where(IsFailedCheck)), fixture, "unavailable");
        Assert.Contains($"fresh={fresh.RunDirectory}",
            Assert.Single(probe.Events.Where(e => e.StartsWith("RUN_CLOSURE_RECOVERED ", StringComparison.Ordinal))),
            StringComparison.Ordinal);
        Assert.Empty(probe.Events.Where(IsClosureEscalation));
        Assert.Empty(probe.Attention);
        Assert.False(File.Exists(Path.Combine(fixture.RunDirectory, DeletedFile)));
    }

    [Xunit.Fact]
    public async Task Damaged_replacement_is_checked_once_and_never_launched()
    {
        using var fixture = new PublishedFixture();
        using var replacement = new PublishedFixture();
        fixture.Delete(DeletedFile);
        replacement.Delete(DeletedFile);
        var probe = new SupervisorProbe(fixture);
        var stageCalls = 0;

        Assert.Equal(1, await probe.RunAsync(_ => { stageCalls++; return replacement.Successor(); }));

        Assert.Equal(1, stageCalls);
        Assert.Empty(probe.Host.Requests);
        var failures = probe.Events.Where(IsFailedCheck).ToArray();
        Assert.Equal(2, failures.Length);
        AssertDiagnostics(failures[0], fixture, "unavailable");
        AssertDiagnostics(failures[1], replacement, "unavailable");
        Assert.Contains("recovery=validation-failed", Assert.Single(probe.Events.Where(IsClosureEscalation)),
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task Publication_exception_escalates_without_launching_or_retrying()
    {
        using var fixture = new PublishedFixture();
        fixture.Delete(DeletedFile);
        var probe = new SupervisorProbe(fixture);
        var stageCalls = 0;

        Assert.Equal(1, await probe.RunAsync(_ => { stageCalls++; throw new IOException("publication refused"); }));

        Assert.Equal(1, stageCalls);
        Assert.Empty(probe.Host.Requests);
        Assert.Single(probe.Events.Where(IsFailedCheck));
        Assert.Contains("recovery=publication-failed", Assert.Single(probe.Events.Where(IsClosureEscalation)),
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task Repaired_original_folder_is_rejected_as_a_recovery_target()
    {
        using var fixture = new PublishedFixture();
        fixture.Delete(DeletedFile);
        var probe = new SupervisorProbe(fixture);
        var stageCalls = 0;

        Assert.Equal(1, await probe.RunAsync(_ =>
        {
            stageCalls++;
            Assert.Equal(fixture.RunDirectory, fixture.Publish());
            return fixture.Successor();
        }));

        Assert.Equal(1, stageCalls);
        Assert.Empty(probe.Host.Requests);
        Assert.Single(probe.Events.Where(IsFailedCheck));
        Assert.Contains("recovery=not-a-fresh-folder", Assert.Single(probe.Events.Where(IsClosureEscalation)),
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task Renewal_rechecks_files_and_uses_the_previously_verified_inventory()
    {
        using var fixture = new PublishedFixture();
        var probe = new SupervisorProbe(fixture);
        probe.Host.OnLaunch = request =>
        {
            fixture.Delete(DeletedFile);
            WriteExit(request, restart: true);
        };

        Assert.Equal(1, await probe.RunAsync());

        Assert.Single(probe.Host.Requests);
        var failed = Assert.Single(probe.Events.Where(IsFailedCheck));
        Assert.Contains("launch=renewal-child", failed, StringComparison.Ordinal);
        AssertDiagnostics(failed, fixture, DeletedFile);
    }

    [Xunit.Fact]
    public async Task Damaged_pending_successor_is_checked_before_activation()
    {
        using var fixture = new PublishedFixture();
        using var successor = new PublishedFixture();
        successor.Delete(DeletedFile);
        var probe = new SupervisorProbe(fixture);
        probe.Host.OnLaunch = request => WriteExit(request, restart: true);
        var stageCalls = 0;

        Assert.Equal(1, await probe.RunAsync(_ =>
            ++stageCalls == 1 ? successor.Successor() : null!));

        Assert.Equal(2, stageCalls); // Planned successor, then one recovery attempt.
        var launched = Assert.Single(probe.Host.Requests);
        Assert.Equal(Path.Combine(fixture.RunDirectory, AppLeaf), launched.CommandPrefix![1]);
        var failed = Assert.Single(probe.Events.Where(IsFailedCheck));
        Assert.Contains("launch=successor", failed, StringComparison.Ordinal);
        AssertDiagnostics(failed, successor, "unavailable");
    }

    private static bool IsFailedCheck(string text) => text.StartsWith("RUN_CLOSURE_CHECK_FAILED ", StringComparison.Ordinal);
    private static bool IsClosureEscalation(string text) => text.StartsWith("SUPERVISOR_ESCALATED reason=run-closure-damaged ", StringComparison.Ordinal);

    private static void AssertDiagnostics(string text, PublishedFixture fixture, string missing)
    {
        Assert.Contains($"folder={fixture.RunDirectory}", text, StringComparison.Ordinal);
        Assert.Contains($"expected={fixture.ExpectedCount}", text, StringComparison.Ordinal);
        Assert.Contains($"actual={fixture.ExpectedCount - 1}", text, StringComparison.Ordinal);
        Assert.Contains($"firstMissing={missing}", text, StringComparison.Ordinal);
    }

    private static void WriteExit(ConductorSupervisorProcessRequest request, bool restart) =>
        ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
            new ConductorContinuityExitArtifact(restart ? "max-duration" : "stop-file", 1, 0, restart));

    private sealed class RecordingHost : IConductorSupervisorProcessHost
    {
        internal List<ConductorSupervisorProcessRequest> Requests { get; } = [];
        internal Action<ConductorSupervisorProcessRequest> OnLaunch { get; set; } = request => WriteExit(request, false);
        public Task<ConductorSupervisorProcessResult> RunAsync(ConductorSupervisorProcessRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            OnLaunch(request);
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 100));
        }
    }

    private sealed class SupervisorProbe(PublishedFixture fixture)
    {
        internal RecordingHost Host { get; } = new();
        internal List<string> Events { get; } = [];
        internal List<string> Attention { get; } = [];
        internal Task<int> RunAsync(Func<CancellationToken, ConductorPreparedSuccessor>? stage = null)
        {
            var ownBuild = new ConductorSupervisorBuildIdentity("commit-original", fixture.RunDirectory);
            var supervisor = new ConductorContinuitySupervisor(Host, new SupervisorHandoffFixture.RecordingEvents(),
                stageSuccessor: stage, appendConductEvent: (_, _, text) => Events.Add(text),
                supervisorHandoff: new(new SupervisorHandoffFixture.FakeHandoffSeam(ownBuild), ownBuild,
                    TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1)),
                dotnetPath: "dotnet-test", inspectStores: () => [],
                raiseEscalationAttention: (_, reason, _) => Attention.Add(reason));
            return TestHangGuard.WaitAsync(supervisor.RunAsync(["conduct", "--loop"], fixture.Root,
                Path.Combine(fixture.Root, "artifacts"), "closure-test", "closure-tenant"), "closure supervisor result");
        }
    }

    private sealed class PublishedFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"run-closure-{Guid.NewGuid():N}");
        internal string RunDirectory { get; }
        internal string MarkerPath => Path.Combine(RunDirectory, ConductorRunClosureCheck.MarkerName);
        internal IReadOnlyCollection<string> Inventory { get; }
        internal int ExpectedCount { get; }
        private readonly string _resolver = Path.Combine(VerifiedRepositoryRoot.Find(), "scripts", "resolve-run-dir.ps1");

        internal PublishedFixture()
        {
            try
            {
                var paths = new[] { AppLeaf, "a-b.dll", "ab.dll", "a_b.dll", "B.dll", DeletedFile,
                    "fr/y.resources.dll", "runtimes/win-x64/native/e_sqlite3.dll", ".hidden" };
                foreach (var relative in paths)
                {
                    var file = Path.Combine(Root, "output", relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, relative, new UTF8Encoding(false));
                }
                File.SetAttributes(Path.Combine(Root, "output", ".hidden"), FileAttributes.Hidden);
                Inventory = paths;
                RunDirectory = Publish();
                using var marker = JsonDocument.Parse(File.ReadAllText(MarkerPath));
                ExpectedCount = marker.RootElement.GetProperty("fileCount").GetInt32();
                Assert.Equal(paths.Length, ExpectedCount);
                Assert.True(File.Exists(Path.Combine(RunDirectory, ".hidden")));
            }
            catch { Dispose(); throw; }
        }

        internal string Publish() => RunPowerShell($"& {Quote(_resolver)} {Quote(Path.Combine(Root, "output", AppLeaf))}").Trim();
        internal void Delete(string relative) => File.Delete(Path.Combine(RunDirectory, relative));
        internal ConductorRunClosureVerdict Check() => ConductorRunClosureCheck.Verify(RunDirectory, AppLeaf, Inventory);
        internal ConductorPreparedSuccessor Successor() => new(RunDirectory, Path.Combine(RunDirectory, AppLeaf),
            "commit-fresh", "commit-fresh", "self-check=true");

        internal bool ScriptVerdict()
        {
            // Extract the actual function bodies and marker constants without running the
            // resolver's top-level publication/repair path against the damaged fixture.
            var script = """
                $tokens = $null; $errors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile($resolver, [ref]$tokens, [ref]$errors)
                if ($errors.Count -ne 0) { throw 'Resolver parse errors' }
                foreach ($name in @('Test-NativeSqliteAssetPresent', 'Get-OutputContentSnapshot', 'Test-PublishedRunDirectory')) {
                    $functions = @($ast.FindAll({ param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
                    }, $true))
                    if ($functions.Count -ne 1) { throw "Missing or duplicate function: $name" }
                    . ([scriptblock]::Create($functions[0].Extent.Text))
                }
                foreach ($name in @('script:RunCacheSchemaVersion', 'script:RunCacheMarkerName')) {
                    $assignments = @($ast.FindAll({ param($node)
                        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                        $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                        $node.Left.VariablePath.UserPath -eq $name
                    }, $true))
                    if ($assignments.Count -ne 1) { throw "Missing or duplicate constant: $name" }
                    . ([scriptblock]::Create($assignments[0].Extent.Text))
                }
                Test-PublishedRunDirectory -Path $run -ExpectedDigest (Split-Path $run -Leaf) -AppLeaf $leaf
                """;
            var result = RunPowerShell($"$resolver = {Quote(_resolver)}; $run = {Quote(RunDirectory)}; $leaf = {Quote(AppLeaf)};\n{script}").Trim();
            Assert.True(result is "True" or "False", $"Validator returned no boolean: {result}");
            return bool.Parse(result);
        }

        private string RunPowerShell(string script)
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var windows = Directory.GetParent(system)!.FullName;
            var start = new ProcessStartInfo(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Environment.Clear();
            start.Environment["SystemRoot"] = windows;
            start.Environment["WINDIR"] = windows;
            start.Environment["PATH"] = system;
            start.Environment["TEMP"] = Root;
            start.Environment["TMP"] = Root;
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-EncodedCommand");
            var command = "$ErrorActionPreference = 'Stop'; " +
                $"[System.Threading.Thread]::CurrentThread.CurrentCulture = [cultureinfo]::GetCultureInfo({Quote(CultureInfo.CurrentCulture.Name)});\n" + script;
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
            var result = TestChildProcessCapture.Run(start, hangBound: TestHangGuard.HangSafetyBound);
            Assert.True(result.ExitCode == 0, $"PowerShell exit={result.ExitCode}: {result.Stderr}");
            return result.Stdout;
        }

        private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}

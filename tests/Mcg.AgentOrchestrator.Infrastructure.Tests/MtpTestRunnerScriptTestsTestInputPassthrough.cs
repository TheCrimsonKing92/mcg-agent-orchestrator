using System.Text.Json;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class MtpTestRunnerScriptTestsTestInputPassthrough
{
    private const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    private const string Alpha = "MCG_TEST_INPUT_ALPHA";
    private const string Beta = "MCG_TEST_INPUT_BETA";

    [Fact]
    public void HermeticEnvironment_ExplicitInput_ReachesChildAndRestoresParent()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var child = Path.Combine(sandbox.Root, "probe.ps1");
        var output = Path.Combine(sandbox.Root, "probe.json");
        var restored = Path.Combine(sandbox.Root, "restored.json");
        File.WriteAllText(child, $$"""
            @{ probe = $env:MCG_TEST_INPUT_PROBE; unrelated = $env:MCG_REPLAY_UNRELATED } |
                ConvertTo-Json -Compress | Set-Content -LiteralPath '{{Quote(output)}}'
            """);
        var result = RunModule(sandbox, $$"""
            $snapshot = Get-MtpEnvironmentSnapshot
            if ($snapshot.ContainsKey('MCG_TEST_INPUT_PROBE')) { throw 'Probe must be absent from snapshot' }
            try {
                $env:MCG_TEST_INPUT_PROBE = 'inherited'
                $env:MCG_REPLAY_UNRELATED = 'must-strip'
                Set-MtpHermeticEnvironment -RepositoryRoot $root -WritableRoot $root -TestInputEnvironment ([ordered]@{ MCG_TEST_INPUT_PROBE = 'supplied' })
                $run = Invoke-MtpAppHost -Executable 'powershell.exe' -Arguments @('powershell.exe','-NoProfile','-ExecutionPolicy','Bypass','-File','{{Quote(child)}}') -OutputLog (Join-Path $root 'probe.log')
                if ($run.ExitCode -ne 0 -or -not $run.ExitConfirmed) { throw 'Probe host failed' }
            } finally {
                Restore-MtpEnvironment -Snapshot $snapshot
                @{ probe = $env:MCG_TEST_INPUT_PROBE; unrelated = $env:MCG_REPLAY_UNRELATED } |
                    ConvertTo-Json -Compress | Set-Content -LiteralPath '{{Quote(restored)}}'
            }
            """);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var probe = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal("supplied", probe.RootElement.GetProperty("probe").GetString());
        Assert.Null(probe.RootElement.GetProperty("unrelated").GetString());
        using var parent = JsonDocument.Parse(File.ReadAllText(restored));
        Assert.Null(parent.RootElement.GetProperty("probe").GetString());
        Assert.Null(parent.RootElement.GetProperty("unrelated").GetString());
    }

    [Theory]
    [InlineData("FOO=1", null)]
    [InlineData("mcg_test_input_x=1", null)]
    [InlineData("MCG_TEST_INPUT_=1", null)]
    [InlineData("MCG_TEST_INPUT_A=1", "MCG_TEST_INPUT_A=2")]
    [InlineData("MCG_TEST_INPUT_B=", null)]
    [InlineData("FOO=hunter-two", null)]
    [InlineData("MCG_TEST_INPUT_MISSING_EQUALS", null)]
    [InlineData(" MCG_TEST_INPUT_A=1", null)]
    [InlineData("MCG_TEST_INPUT_A\n=1", null)]
    public void Summary_InvalidInput_RejectsBeforeCreatingResults(string first, string? second)
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var inputs = second is null ? new[] { first } : new[] { first, second };
        var result = RunSummary(sandbox, inputs);
        Assert.Equal(26, result.ExitCode);
        Assert.Contains("TEST INPUT FAILURE", result.Stdout);
        Assert.DoesNotContain("Results directory:", result.Stdout);
        Assert.Empty(Directory.EnumerateFileSystemEntries(sandbox.ResultsRoot));
        var separator = first.IndexOf('=');
        Assert.Contains(separator < 0 ? first : first[..separator], result.Stdout);
        Assert.DoesNotContain("hunter-two", result.Stdout + result.Stderr);
        using var summary = ReadSummary(result.Stdout);
        Assert.Empty(summary.RootElement.GetProperty("testInputNames").EnumerateArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Summary_ExplicitInputs_ReportsNamesAndStripsInheritedValues(bool supplyInputs)
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var output = Path.Combine(sandbox.Root, "test-inputs.json");
        // Stub build/closure work, retaining the public wrapper, scrub, TRX checks and summary.
        // The separate child probe above covers real process environment inheritance.
        File.AppendAllText(Path.Combine(sandbox.Root, "scripts", "MtpTestRunner.psm1"), $$"""

            function Resolve-MtpFaultDialogStartupHook { return '' }
            function Invoke-MtpBuild { return $true }
            function Get-MtpClosureManagedAssemblyPath { return $script:TestInputProbeAssembly }
            function Assert-MtpSealedClosure { }
            function Invoke-MtpAppHost {
                param($Executable, $Arguments, $OutputLog, $StartupHookPath, $BuildOutputRoot, $AcceptanceAttributionReplayRequest, $AllowBreakaway, $TestHostTimeoutSeconds, $TimeoutPolicy)
                @{ alpha = $env:MCG_TEST_INPUT_ALPHA; beta = $env:MCG_TEST_INPUT_BETA } |
                    ConvertTo-Json -Compress | Set-Content -LiteralPath '{{Quote(output)}}'
                $dirIndex = [Array]::IndexOf($Arguments, '--results-directory')
                $trxIndex = [Array]::IndexOf($Arguments, '--report-trx-filename')
                '<TestRun><Results><UnitTestResult testName="GoalWorktreeTests.SupportsGoalWorktrees" outcome="Passed" /></Results><ResultSummary><Counters total="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>' | Set-Content -LiteralPath (Join-Path $Arguments[$dirIndex+1] $Arguments[$trxIndex+1])
                return [pscustomobject]@{ExitCode=0;OwnedProcessId=1;ExitConfirmed=$true;TimedOut=$false;RunnerFailed=$false}
            }
            $script:TestInputProbeAssembly = Join-Path $PSScriptRoot 'probe.dll'
            [IO.File]::WriteAllText($script:TestInputProbeAssembly, 'managed closure probe')
            """ + Environment.NewLine);
        const string alphaValue = "alpha-secret-1";
        const string betaValue = " beta=secret,2 ";
        var inputs = supplyInputs ? new[] { $"{Alpha}={alphaValue}", $"{Beta}={betaValue}" } : null;
        var result = RunSummary(sandbox, inputs, inheritInputs: true, filter: "FullyQualifiedName~GoalWorktreeTests");
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var probe = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal(supplyInputs ? alphaValue : null, probe.RootElement.GetProperty("alpha").GetString());
        Assert.Equal(supplyInputs ? betaValue : null, probe.RootElement.GetProperty("beta").GetString());
        using var summary = ReadSummary(result.Stdout);
        var names = summary.RootElement.GetProperty("testInputNames").EnumerateArray().Select(value => value.GetString()).ToArray();
        if (supplyInputs)
        {
            Assert.Equal(new[] { Alpha, Beta }, names);
            Assert.Contains($"Test inputs passed: {Alpha}, {Beta}", result.Stdout);
            var lines = result.Stdout.Split('\n', StringSplitOptions.TrimEntries);
            var resultsLine = Array.FindIndex(lines, line => line.StartsWith("Results directory:", StringComparison.Ordinal));
            Assert.True(resultsLine >= 0);
            Assert.Equal($"Test inputs passed: {Alpha}, {Beta}", lines[resultsLine + 1]);
        }
        else
        {
            Assert.Empty(names);
            Assert.DoesNotContain("Test inputs passed:", result.Stdout);
        }
        foreach (var secret in new[] { alphaValue, betaValue, "inherited-alpha", "inherited-beta" })
            Assert.DoesNotContain(secret, result.Stdout + result.Stderr);
    }

    [Fact]
    public void HermeticEnvironment_UnreservedMapKey_RejectsBeforeScrubbing()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("success");
        var result = RunModule(sandbox, """
            $env:MCG_REPLAY_UNRELATED = 'unchanged'
            $rejected = $false
            try { Set-MtpHermeticEnvironment -RepositoryRoot $root -WritableRoot $root -TestInputEnvironment @{ HOME = 'hunter-two' } }
            catch {
                $rejected = $true
                if ($_.Exception.Message -notmatch 'HOME' -or $_.Exception.Message.Contains('hunter-two')) { throw 'Unsafe key diagnostic' }
            }
            if (-not $rejected -or $env:MCG_REPLAY_UNRELATED -ne 'unchanged') { throw 'Unsafe map changed environment' }
            """);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
    }

    [Fact]
    public void Runbook_ManagedTestRules_DocumentInputsAndConsumerUsesReservedName()
    {
        var root = MtpTestRunnerScriptTests.RepositoryRoot();
        var runbook = File.ReadAllText(Path.Combine(root, "docs", "operator-runbook.md"));
        var start = runbook.IndexOf("Scope the test suite", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = runbook.IndexOf("### Managed test filter syntax", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var rules = runbook[start..end];
        foreach (var token in new[] { "-TestInput", "MCG_TEST_INPUT_", "MTP_TERMINAL_SUMMARY" })
            Assert.Contains(token, rules);
        var consumer = File.ReadAllText(Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "GroupedGateAttemptLegacyRestoreTests.cs"));
        Assert.Contains("MCG_TEST_INPUT_GROUPED_GATE_ATTEMPTS_COPY", consumer);
        Assert.DoesNotContain("\"MCG_GROUPED_GATE_ATTEMPTS_COPY\"", consumer);
    }

    private static MtpTestRunnerScriptTests.ProcessResult RunSummary(
        MtpTestRunnerScriptTests.ScriptSandbox sandbox, string[]? inputs, bool inheritInputs = false, string filter = "")
    {
        var start = sandbox.SandboxPowerShellStartInfo();
        if (inheritInputs)
        {
            start.Environment[Alpha] = "inherited-alpha";
            start.Environment[Beta] = "inherited-beta";
        }
        var inputArgument = inputs is null ? "" : " -TestInput @(" + string.Join(",", inputs.Select(value => $"'{Quote(value)}'")) + ")";
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"& '{Quote(Path.Combine(sandbox.Root, "scripts", "Invoke-TestSummary.ps1"))}' -Target '{Project}' -ResultsRoot '{Quote(sandbox.ResultsRoot)}' -Filter '{Quote(filter)}'{inputArgument}; exit $LASTEXITCODE");
        return MtpTestRunnerScriptTests.Run(start);
    }

    private static MtpTestRunnerScriptTests.ProcessResult RunModule(MtpTestRunnerScriptTests.ScriptSandbox sandbox, string body)
    {
        var start = sandbox.SandboxPowerShellStartInfo();
        start.Environment.Remove("MCG_TEST_INPUT_PROBE");
        start.Environment.Remove("MCG_REPLAY_UNRELATED");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"$ErrorActionPreference='Stop'; Import-Module '{Quote(Path.Combine(sandbox.Root, "scripts", "MtpTestRunner.psm1"))}' -Force; & (Get-Module MtpTestRunner) {{ $root='{Quote(sandbox.Root)}'; {body} }}");
        return MtpTestRunnerScriptTests.Run(start);
    }

    private static JsonDocument ReadSummary(string stdout)
    {
        var line = Assert.Single(stdout.Split('\n').Where(value => value.StartsWith("MTP_TERMINAL_SUMMARY ", StringComparison.Ordinal)));
        return JsonDocument.Parse(line["MTP_TERMINAL_SUMMARY ".Length..]);
    }

    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}

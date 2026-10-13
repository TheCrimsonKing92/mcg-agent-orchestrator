using System.Text.Json;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class MtpAcceptanceAttributionReplayTests
{
    private const string Method = "ApparatusRedGateSourceIndexTests.ActualCopiedCompletionRecordsOneFailedDecisionAndNextGoalCanAdvance";
    private const string Filter = "FullyQualifiedName~ApparatusRedGateSourceIndexTests&Name~ActualCopiedCompletionRecordsOneFailedDecisionAndNextGoalCanAdvance";

    [Fact]
    public void ChildReceivesScopedRequestAfterSanitizationWithoutChangingParentOrBuildEnvironment()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("passed");
        var child = Path.Combine(sandbox.Root, "probe.ps1");
        var output = Path.Combine(sandbox.Root, "probe.json");
        File.WriteAllText(child, "$probe = @{ mode = $env:MCG_ACCEPTANCE_ATTRIBUTION_REPLAY_MODE; request = $env:MCG_ACCEPTANCE_ATTRIBUTION_REPLAY_REQUEST; secret = $env:MCG_REPLAY_UNRELATED }; $probe | ConvertTo-Json -Compress | Set-Content -LiteralPath '" + Quote(output) + "'");
        var result = RunModule(sandbox, $$"""
            $snapshot = Get-MtpEnvironmentSnapshot
            try {
                $env:MCG_REPLAY_UNRELATED = 'must-strip'
                $env:MCG_ACCEPTANCE_ATTRIBUTION_REPLAY_REQUEST = 'inherited-must-strip'
                Set-MtpHermeticEnvironment -RepositoryRoot $root -WritableRoot $root
                $build = New-MtpProcessStartInfo -Executable 'powershell.exe' -Arguments @('powershell.exe')
                if ($build.EnvironmentVariables['MCG_ACCEPTANCE_ATTRIBUTION_REPLAY_REQUEST']) { throw 'Build inherited replay context' }
                $run = Invoke-MtpAppHost -Executable 'powershell.exe' -Arguments @('powershell.exe','-NoProfile','-File','{{Quote(child)}}') -OutputLog (Join-Path $root 'probe.log') -AcceptanceAttributionReplayRequest 'bound-request'
                if ($run.ExitCode -ne 0 -or -not $run.ExitConfirmed) { throw 'Probe host failed' }
                if ($env:MCG_ACCEPTANCE_ATTRIBUTION_REPLAY_REQUEST) { throw 'Parent environment changed' }
            } finally { Restore-MtpEnvironment -Snapshot $snapshot }
            """);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var probe = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal("actual-copy", probe.RootElement.GetProperty("mode").GetString());
        Assert.Equal("bound-request", probe.RootElement.GetProperty("request").GetString());
        Assert.Null(probe.RootElement.GetProperty("secret").GetString());
    }

    [Fact]
    public void InvocationAllowsOnlyExactManagedReplayAndRejectsBroadFiltersRepeatsOrOverrides()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("passed");
        var result = RunModule(sandbox, $$"""
            $projects = @([pscustomobject]@{project='tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj'})
            Assert-MtpAttributionReplayInvocation -Projects $projects -Filters @('{{Filter}}') -Repeat 1
            $rejected = 0
            foreach ($case in @(@{Filters=@('Name~ActualCopiedCompletion');Repeat=1}, @{Filters=@('{{Filter}}');Repeat=2}, @{Filters=@('{{Filter}}');Repeat=1;RunnerPath='fake.exe'}, @{Filters=@('{{Filter}}','');Repeat=1})) {
                try { Assert-MtpAttributionReplayInvocation -Projects $projects @case } catch { $rejected++ }
            }
            if ($rejected -ne 4) { throw 'Unscoped replay was accepted' }
            """);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
    }

    [Fact]
    public void HelperBindsFinalGitCandidateInputAssemblyAndSourcesAndRejectsStaleOrDirtyRoot()
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("passed");
        var result = RunModule(sandbox, """
            $paths = @('src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceTestSourceResolver.cs',
                'src/Mcg.AgentOrchestrator.App/Orchestration/ApparatusRedGate.cs',
                'tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AcceptanceAttributionReplayFixture.cs',
                'tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ApparatusRedGateSourceIndexTests.cs')
            foreach ($path in $paths) {
                $full = Join-Path $root $path
                [void][IO.Directory]::CreateDirectory((Split-Path -Parent $full))
                [IO.File]::WriteAllText($full, '// source sentinel')
            }
            & git -C $root init -q
            if ($LASTEXITCODE -ne 0) { throw 'Fixture git init failed' }
            & git -C $root add -- src tests scripts config
            & git -C $root -c user.name=ReplayFixture -c user.email=replay@example.test commit -qm fixture
            if ($LASTEXITCODE -ne 0) { throw 'Fixture git commit failed' }
            $head = [string](& git -C $root rev-parse HEAD)
            $inputPath = Join-Path $root 'input.json'
            $assembly = Join-Path $root 'probe.dll'
            [IO.File]::WriteAllText($assembly, 'assembly sentinel')
            @{sourceRoot=$root;finalCandidateSha=$head} | ConvertTo-Json | Set-Content -LiteralPath $inputPath
            $request = New-MtpAttributionReplayRequest -InputPath $inputPath -RepositoryRoot $root -ManagedAssembly $assembly -ResultPath (Join-Path $root 'report.json') | ConvertFrom-Json
            if ($request.finalCandidateSha -ne $head -or $request.inputSha256 -ne (Get-MtpSha256File $inputPath) -or $request.assemblySha256 -ne (Get-MtpSha256File $assembly) -or $request.sources.Count -ne 6) { throw 'Final candidate bindings missing' }
            foreach ($source in $request.sources) {
                if ($source.sha256 -ne (Get-MtpSha256File (Join-Path $root $source.file))) { throw 'Source checksum mismatch' }
            }
            @{sourceRoot=$root;finalCandidateSha=('0'*40)} | ConvertTo-Json | Set-Content -LiteralPath $inputPath
            $staleRejected = $false
            try { New-MtpAttributionReplayRequest -InputPath $inputPath -RepositoryRoot $root -ManagedAssembly $assembly -ResultPath (Join-Path $root 'report.json') } catch { $staleRejected = $true }
            if (-not $staleRejected) { throw 'Stale candidate accepted' }
            @{sourceRoot=$root;finalCandidateSha=$head} | ConvertTo-Json | Set-Content -LiteralPath $inputPath
            [IO.File]::AppendAllText((Join-Path $root $paths[0]), '// changed')
            $dirtyRejected = $false
            try { New-MtpAttributionReplayRequest -InputPath $inputPath -RepositoryRoot $root -ManagedAssembly $assembly -ResultPath (Join-Path $root 'report.json') } catch { $dirtyRejected = $true }
            if (-not $dirtyRejected) { throw 'Dirty candidate accepted' }
            """);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
    }

    [Theory]
    [InlineData("actual-copy", true)]
    [InlineData("missing", false)]
    [InlineData("stale", false)]
    [InlineData("normalized", false)]
    public void SummaryTransmitsExplicitRequestAndRequiresBoundRetainedArtifact(string behavior, bool expectedPass)
    {
        using var sandbox = MtpTestRunnerScriptTests.ScriptSandbox.Create("passed");
        // Stub build/closure production only; exercise the public wrapper, sanitization, request
        // delivery, TRX checks, result validation, retention, and terminal decision in one run.
        // Real ProcessStartInfo delivery is independently exercised by the probe above.
        var module = Path.Combine(sandbox.Root, "scripts", "MtpTestRunner.psm1");
        File.AppendAllText(module, $$"""

            function Resolve-MtpFaultDialogStartupHook { return '' }
            function Invoke-MtpBuild { return $true }
            function Get-MtpClosureManagedAssemblyPath { return $script:ReplayProbeAssembly }
            function Assert-MtpSealedClosure { }
            function New-MtpAttributionReplayRequest {
                param($InputPath, $RepositoryRoot, $ManagedAssembly, $ResultPath)
                if ($InputPath -ne 'explicit-input' -or $env:MCG_ACCEPTANCE_ATTRIBUTION_REPLAY_REQUEST) { throw 'Request not scoped after sanitization' }
                return (@{requestId='this-invocation';resultPath=$ResultPath} | ConvertTo-Json -Compress)
            }
            function Invoke-MtpAppHost {
                param($Executable, $Arguments, $OutputLog, $StartupHookPath, $BuildOutputRoot, $AcceptanceAttributionReplayRequest, $AllowBreakaway, $TestHostTimeoutSeconds, $TimeoutPolicy)
                if ([string]::IsNullOrWhiteSpace($AcceptanceAttributionReplayRequest)) { throw 'Helper dropped requested replay' }
                $request = $AcceptanceAttributionReplayRequest | ConvertFrom-Json
                $dirIndex = [Array]::IndexOf($Arguments, '--results-directory')
                $trxIndex = [Array]::IndexOf($Arguments, '--report-trx-filename')
                '<TestRun><Results><UnitTestResult testName="{{Method}}" outcome="Passed" /></Results><ResultSummary><Counters total="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>' | Set-Content -LiteralPath (Join-Path $Arguments[$dirIndex+1] $Arguments[$trxIndex+1])
                if ('{{behavior}}' -ne 'missing') {
                    $report = @{ replayKind='actual-copy'; request=$request; historicalAttemptId='attempt'; originalTrxRunId='original'; rerunTrxRunId='rerun'; receipt=@{failureDecisions=1;nextGoalAdvanced=$true;sourceEnumerations=0;sourceReads=0} }
                    if ('{{behavior}}' -eq 'stale') { $report.request = @{requestId='old-invocation'} }
                    if ('{{behavior}}' -eq 'normalized') { $report.replayKind='normalized' }
                    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $request.resultPath
                }
                return [pscustomobject]@{ExitCode=0;OwnedProcessId=1;ExitConfirmed=$true;TimedOut=$false;RunnerFailed=$false}
            }
            $script:ReplayProbeAssembly = Join-Path $PSScriptRoot 'probe.dll'
            [IO.File]::WriteAllText($script:ReplayProbeAssembly, 'managed closure probe')
            """ + Environment.NewLine);
        var start = sandbox.SandboxPowerShellStartInfo();
        start.Environment[AcceptanceAttributionReplayFixture.RequestVariable] = "inherited-must-strip";
        foreach (var arg in new[] { "-File", Path.Combine(sandbox.Root, "scripts", "Invoke-TestSummary.ps1"), "-Target",
                     "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                     "-Filter", Filter, "-AcceptanceAttributionReplayInput", "explicit-input", "-ResultsRoot", sandbox.ResultsRoot })
            start.ArgumentList.Add(arg);
        var result = MtpTestRunnerScriptTests.Run(start);
        Assert.Equal(expectedPass, result.ExitCode == 0);
        var retained = Directory.GetFiles(sandbox.ResultsRoot, "run-identity.json", SearchOption.AllDirectories);
        if (!expectedPass)
        {
            Assert.Empty(retained);
            Assert.Contains("EVIDENCE RETENTION FAILURE", result.Stdout);
            return;
        }
        var identity = Assert.Single(retained);
        using var json = JsonDocument.Parse(File.ReadAllText(identity));
        var artifact = Path.Combine(Path.GetDirectoryName(identity)!, json.RootElement.GetProperty("acceptanceAttributionReplay").GetProperty("file").GetString()!);
        Assert.True(File.Exists(artifact));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(artifact))),
            json.RootElement.GetProperty("acceptanceAttributionReplay").GetProperty("sha256").GetString(), ignoreCase: true);
        Assert.Contains("actual-copy", File.ReadAllText(artifact));
    }

    private static MtpTestRunnerScriptTests.ProcessResult RunModule(MtpTestRunnerScriptTests.ScriptSandbox sandbox, string body)
    {
        var module = Quote(Path.Combine(sandbox.Root, "scripts", "MtpTestRunner.psm1"));
        return MtpTestRunnerScriptTests.RunPowerShellCommand(sandbox.Root,
            $"$ErrorActionPreference='Stop'; Import-Module '{module}' -Force; & (Get-Module MtpTestRunner) {{ $root='{Quote(sandbox.Root)}'; {body} }}");
    }
    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}

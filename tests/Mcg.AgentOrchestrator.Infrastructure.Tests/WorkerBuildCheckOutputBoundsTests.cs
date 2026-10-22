using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class WorkerBuildCheckOutputBoundsTests
{
    // Hang-only bound must cover two 270-second child guards in series plus 60 seconds of setup.
    private const int FixtureHangBoundSeconds = 600;

    [Xunit.Fact(DisplayName = "WorkerBuildCheck_warning_heavy_success_bounds_model_visible_output")]
    public async Task WorkerBuildCheckWarningHeavySuccessBoundsModelVisibleOutput()
    {
        var result = await RunFixtureAsync("Warnings");

        Assert.True(result.ExitCode == 0, result.Combined);
        var receipt = Regex.Match(
            result.Stdout,
            @"raw_chars=(?<raw>\d+) helper_chars=(?<helper>\d+) raw_exit=(?<rawExit>\d+) helper_exit=(?<helperExit>\d+) raw_errors=(?<rawErrors>\d+) helper_errors=(?<helperErrors>\d+)",
            RegexOptions.CultureInvariant);
        Assert.True(receipt.Success, result.Combined);
        Assert.True(int.Parse(receipt.Groups["raw"].Value) >= 256_000, result.Combined);
        Assert.True(int.Parse(receipt.Groups["helper"].Value) < 2_048, result.Combined);
        Assert.Equal(receipt.Groups["rawExit"].Value, receipt.Groups["helperExit"].Value);
        Assert.Equal(receipt.Groups["rawErrors"].Value, receipt.Groups["helperErrors"].Value);
        Assert.Equal("0", receipt.Groups["helperErrors"].Value);
    }

    [Xunit.Theory(DisplayName = "WorkerBuildCheck_preserves_bounded_failure_and_multi_project_contracts")]
    [Xunit.InlineData("CompilerFailure")]
    [Xunit.InlineData("NonDiagnosticFailure")]
    [Xunit.InlineData("MultipleProjects")]
    [Xunit.InlineData("MultipleProjectsOneFails")]
    [Xunit.InlineData("LogUnavailable")]
    [Xunit.InlineData("ScopeBootstrapFailure")]
    public async Task WorkerBuildCheckPreservesBoundedFailureAndMultiProjectContracts(string scenario)
    {
        var result = await RunFixtureAsync(scenario);

        Assert.True(result.ExitCode == 0, result.Combined);
        Assert.Contains($"PASS fixture: scenario={scenario}", result.Stdout, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "WorkerBuildDiagnostic_uses_argument_list_closed_stdin_and_durable_summary")]
    public void WorkerBuildDiagnosticUsesArgumentListClosedStdinAndDurableSummary()
    {
        var repositoryRoot = FindRepositoryRoot();
        var helper = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "Invoke-WorkerBuildCheck.ps1"));
        var diagnostic = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "Invoke-WorkerBuildDiagnostic.ps1"));

        Assert.Contains("-clp:ErrorsOnly", helper, StringComparison.Ordinal);
        Assert.Contains("-tl:off", helper, StringComparison.Ordinal);
        Assert.Contains("\"-fl\"", helper, StringComparison.Ordinal);
        Assert.Contains("-flp:LogFile=$logPath;Verbosity=normal;Encoding=UTF-8;Append=false", helper, StringComparison.Ordinal);
        Assert.Contains("complete-log:", helper, StringComparison.Ordinal);
        Assert.Contains("$startInfo.ArgumentList.Add($argument)", diagnostic, StringComparison.Ordinal);
        Assert.Contains("$startInfo.RedirectStandardInput = $true", diagnostic, StringComparison.Ordinal);
        Assert.Contains("$process.StandardInput.Close()", diagnostic, StringComparison.Ordinal);
        Assert.Contains("ReadToEndAsync()", diagnostic, StringComparison.Ordinal);
        Assert.Contains("(not worker build evidence)", diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void FixtureChildGuardsAndLauncherBoundKeepHangOnlyContract()
    {
        const string inspect = """
            using namespace System.Management.Automation.Language
            $ErrorActionPreference = 'Stop'
            function Get-FixtureReport([hashtable]$scripts) {
                $entries = @{}
                $outputBoundsRange = @()
                foreach ($name in $scripts.Keys) {
                    $tokens = $null
                    $parseErrors = $null
                    $ast = [Parser]::ParseInput($scripts[$name], [ref]$tokens, [ref]$parseErrors)
                    if ($parseErrors.Count -ne 0) { throw "$name parse errors: $parseErrors" }
                    $functions = @($ast.FindAll({ param($node)
                        $node -is [FunctionDefinitionAst] -and $node.Name -eq 'Invoke-CapturedProcess'
                    }, $true))
                    if ($functions.Count -ne 1) { $entries[$name] = 'function-count'; continue }
                    $body = $functions[0].Body
                    $parameters = @($body.ParamBlock.Parameters | Where-Object {
                        $_.Name.VariablePath.UserPath -eq 'TimeoutSeconds'
                    })
                    $issues = [System.Collections.Generic.List[string]]::new()
                    if ($parameters.Count -ne 1) { $issues.Add('parameter-count') }
                    else {
                        $parameter = $parameters[0]
                        $integerTypes = @($parameter.Attributes | Where-Object {
                            $_ -is [TypeConstraintAst] -and $_.TypeName.FullName -eq 'int'
                        })
                        if ($integerTypes.Count -ne 1 -or $parameter.StaticType -ne [int] -or
                            $parameter.DefaultValue -isnot [ConstantExpressionAst] -or
                            $parameter.DefaultValue.Value -isnot [int] -or
                            $parameter.DefaultValue.Value -ne 270) { $issues.Add('timeout-default') }
                        if ($name -eq 'OutputBounds') {
                            $ranges = @($parameter.Attributes | Where-Object { $_.TypeName.FullName -eq 'ValidateRange' })
                            if ($ranges.Count -ne 1) { $issues.Add('range-count') }
                            else {
                                $arguments = @($ranges[0].PositionalArguments)
                                if ($arguments.Count -ne 2 -or
                                    $arguments[0] -isnot [ConstantExpressionAst] -or
                                    $arguments[1] -isnot [ConstantExpressionAst] -or
                                    $arguments[0].Value -isnot [int] -or $arguments[1].Value -isnot [int] -or
                                    $arguments[0].Value -ne 1 -or $arguments[1].Value -ne 300) { $issues.Add('range') }
                                $outputBoundsRange = @($arguments | ForEach-Object { $_.Value })
                            }
                        }
                    }
                    $calls = @($body.FindAll({ param($node)
                        $node -is [InvokeMemberExpressionAst] -and $node.Expression -is [VariableExpressionAst] -and
                        $node.Expression.VariablePath.UserPath -eq 'process'
                    }, $true))
                    $waits = @($calls | Where-Object {
                        $_.Member.Value -eq 'WaitForExit' -and $_.Arguments.Count -eq 1 -and
                        $_.Arguments[0] -is [BinaryExpressionAst] -and $_.Arguments[0].Operator -eq [TokenKind]::Multiply -and
                        $_.Arguments[0].Left -is [VariableExpressionAst] -and
                        $_.Arguments[0].Left.VariablePath.UserPath -eq 'TimeoutSeconds' -and
                        $_.Arguments[0].Right -is [ConstantExpressionAst] -and
                        $_.Arguments[0].Right.Value -is [int] -and $_.Arguments[0].Right.Value -eq 1000
                    })
                    if ($waits.Count -eq 0) { $issues.Add('wait') }
                    $kills = @($calls | Where-Object {
                        $_.Member.Value -eq 'Kill' -and $_.Arguments.Count -eq 1 -and
                        $_.Arguments[0] -is [VariableExpressionAst] -and $_.Arguments[0].VariablePath.UserPath -eq 'true'
                    })
                    if ($kills.Count -eq 0) { $issues.Add('kill') }
                    if (@($body.FindAll({ param($node) $node -is [ThrowStatementAst] }, $true)).Count -eq 0) {
                        $issues.Add('throw')
                    }
                    $overrides = @($ast.FindAll({ param($node)
                        $node -is [CommandAst] -and $node.GetCommandName() -eq 'Invoke-CapturedProcess' -and
                        @($node.CommandElements | Where-Object {
                            $_ -is [CommandParameterAst] -and $_.ParameterName -eq 'TimeoutSeconds'
                        }).Count -ne 0
                    }, $true))
                    if ($overrides.Count -ne 0) { $issues.Add('timeout-override') }
                    if ($issues.Count -ne 0) { $entries[$name] = $issues -join ', ' }
                }
                [pscustomobject]@{ Entries = $entries; OutputBoundsRange = $outputBoundsRange }
            }
            $scripts = @{}
            foreach ($name in @('OutputBounds', 'FreeSlot', 'RunHold')) {
                $path = Join-Path $env:MCG_GUARD_FIXTURES "WorkerBuildCheck$name/Invoke-Fixture.ps1"
                $scripts[$name] = [System.IO.File]::ReadAllText($path)
            }
            $baseline = Get-FixtureReport $scripts
            $mutations = @(
                @{ Fixture = 'FreeSlot'; Before = '[int]$TimeoutSeconds = 270'; After = '[int]$TimeoutSeconds = 120' }
                @{ Fixture = 'OutputBounds';
                   Before = '$helper = Invoke-CapturedProcess -FileName $shellPath -ArgumentList $helperArguments -WorkingDirectory $workDirectory';
                   After = '$helper = Invoke-CapturedProcess -FileName $shellPath -ArgumentList $helperArguments -WorkingDirectory $workDirectory -TimeoutSeconds 120' }
                @{ Fixture = 'RunHold'; Before = '            $process.Kill($true)'; After = '' }
            )
            $controls = @{}
            foreach ($mutation in $mutations) {
                $copy = $scripts.Clone()
                $original = $copy[$mutation.Fixture]
                if (-not $original.Contains($mutation.Before)) { throw "Mutation target missing: $($mutation.Fixture)" }
                $copy[$mutation.Fixture] = $original.Replace($mutation.Before, $mutation.After)
                if ($copy[$mutation.Fixture] -eq $original) { throw "Mutation ineffective: $($mutation.Fixture)" }
                $controls[$mutation.Fixture] = Get-FixtureReport $copy
            }
            [pscustomobject]@{ Baseline = $baseline; Controls = $controls } | ConvertTo-Json -Depth 6 -Compress
            """;
        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            WorkingDirectory = FindRepositoryRoot(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", inspect })
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["MCG_GUARD_FIXTURES"] = Path.Combine(
            FindRepositoryRoot(), "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Fixtures");

        var capture = TestChildProcessCapture.Run(startInfo);
        Assert.True(capture.ExitCode == 0, capture.Stdout + capture.Stderr);
        Assert.True(string.IsNullOrWhiteSpace(capture.Stderr), capture.Stderr);
        using var parsed = JsonDocument.Parse(capture.Stdout);
        var baseline = parsed.RootElement.GetProperty("Baseline");
        Assert.Empty(baseline.GetProperty("Entries").EnumerateObject());
        Assert.Equal(new[] { 1, 300 },
            baseline.GetProperty("OutputBoundsRange").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.True(FixtureHangBoundSeconds >= 2 * 270 + 60);
        foreach (var (fixture, issue) in new[]
        {
            ("FreeSlot", "timeout-default"), ("OutputBounds", "timeout-override"), ("RunHold", "kill")
        })
        {
            var mutated = parsed.RootElement.GetProperty("Controls").GetProperty(fixture).GetProperty("Entries");
            AssertOnlyReportDifference(baseline.GetProperty("Entries"), mutated, fixture);
            Assert.Equal(issue, mutated.GetProperty(fixture).GetString());
        }
    }

    private static void AssertOnlyReportDifference(JsonElement baseline, JsonElement mutated, string expected)
    {
        var difference = mutated.EnumerateObject().Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        difference.SymmetricExceptWith(baseline.EnumerateObject().Select(entry => entry.Name));
        Assert.True(difference.SetEquals([expected]), mutated.GetRawText());
    }

    private static async Task<ProcessResult> RunFixtureAsync(string scenario)
    {
        var repositoryRoot = FindRepositoryRoot();
        var fixturePath = Path.Combine(
            repositoryRoot,
            "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "Fixtures",
            "WorkerBuildCheckOutputBounds",
            "Invoke-Fixture.ps1");
        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", fixturePath, "-Scenario", scenario
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var capture = await Task.Run(() => TestChildProcessCapture.Run(
            startInfo, hangBound: TimeSpan.FromSeconds(FixtureHangBoundSeconds)));
        return new ProcessResult(capture.ExitCode, capture.Stdout, capture.Stderr);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
    {
        public string Combined => Stdout + Environment.NewLine + Stderr;
    }
}

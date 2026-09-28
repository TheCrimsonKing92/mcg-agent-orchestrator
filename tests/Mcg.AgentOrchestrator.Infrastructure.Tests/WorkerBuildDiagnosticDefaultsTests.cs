using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class WorkerBuildDiagnosticDefaultsTests
{
    [Xunit.Fact]
    public void TimeoutSecondsDefaultsTo900AndKeepsOverrideRange()
    {
        var script = Path.Combine(MtpTestRunnerScriptTests.RepositoryRoot(), "scripts", "Invoke-WorkerBuildDiagnostic.ps1");
        const string inspect = """
            $tokens = $null
            $parseErrors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($env:MCG_DIAGNOSTIC_SCRIPT, [ref]$tokens, [ref]$parseErrors)
            if ($parseErrors.Count -ne 0) { throw ($parseErrors | Out-String) }
            $parameter = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'TimeoutSeconds' })
            if ($parameter.Count -ne 1) { throw 'Expected one TimeoutSeconds parameter' }
            $range = @($parameter[0].Attributes | Where-Object { $_.TypeName.FullName -eq 'ValidateRange' })
            if ($range.Count -ne 1) { throw 'Expected one ValidateRange attribute' }
            [pscustomobject]@{
                Default = $parameter[0].DefaultValue.Extent.Text
                Type = @($parameter[0].Attributes | Where-Object { $_.TypeName.FullName -eq 'int' }).Count
                Range = @($range[0].PositionalArguments | ForEach-Object { $_.Extent.Text })
            } | ConvertTo-Json -Compress
            """;
        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            WorkingDirectory = MtpTestRunnerScriptTests.RepositoryRoot(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(inspect);
        startInfo.Environment["MCG_DIAGNOSTIC_SCRIPT"] = script;

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stdout + stderr);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
        using var parsed = JsonDocument.Parse(stdout);
        var result = parsed.RootElement;
        Assert.Equal("900", result.GetProperty("Default").GetString());
        Assert.Equal(1, result.GetProperty("Type").GetInt32());
        Assert.Equal(new[] { "1", "3600" },
            result.GetProperty("Range").EnumerateArray().Select(value => value.GetString()).ToArray());
    }
}

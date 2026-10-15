using System.Diagnostics;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class MtpTestRunnerScriptTestsAttributionReplayExecutionPolicy
{
    [Fact]
    public async Task ScriptRunsWithExecutionPolicyBypassUnderRestrictedEnvironment()
    {
        var result = await RunScriptAsync(bypass: true);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.True(result.MarkerExists, "Bypassed script did not write its marker. " + result.Output);
    }

    [Fact]
    public async Task ScriptIsBlockedWithoutExecutionPolicyBypassUnderRestrictedEnvironment()
    {
        var result = await RunScriptAsync(bypass: false);

        Assert.True(result.ExitCode != 0, result.Output);
        Assert.False(result.MarkerExists, "Restricted script wrote its marker. " + result.Output);
    }

    private static async Task<(int ExitCode, bool MarkerExists, string Output)> RunScriptAsync(bool bypass)
    {
        var root = Path.Combine(Path.GetTempPath(), "attribution-replay-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = Path.Combine(root, "probe.ps1");
            var marker = Path.Combine(root, "marker.txt");
            File.WriteAllText(script, "Set-Content -LiteralPath '" + marker.Replace("'", "''") + "' -Value 'ran'");
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            if (bypass)
            {
                startInfo.ArgumentList.Add("-ExecutionPolicy");
                startInfo.ArgumentList.Add("Bypass");
            }
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(script);
            startInfo.Environment["PSExecutionPolicyPreference"] = "Restricted";

            using var process = new Process { StartInfo = startInfo };
            Assert.True(process.Start(), "PowerShell probe process did not start.");
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(), stdout, stderr)
                    .WaitAsync(TimeSpan.FromSeconds(60));
            }
            catch (TimeoutException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                throw new TimeoutException("PowerShell probe did not exit and drain its output before the hang guard.");
            }

            return (process.ExitCode, File.Exists(marker),
                $"Exit code: {process.ExitCode}\nstdout: {await stdout}\nstderr: {await stderr}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

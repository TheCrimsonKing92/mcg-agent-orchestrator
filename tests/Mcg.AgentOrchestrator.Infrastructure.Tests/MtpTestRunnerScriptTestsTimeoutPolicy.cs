using System.Diagnostics;
using System.Text.Json;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class MtpTestRunnerScriptTestsTimeoutPolicy
{
    [Xunit.Fact]
    public void TimeoutPolicyUsesDefaultsAndValidChildOnlyOverrides()
    {
        using var defaults = ReadPolicy(null, null);
        AssertPolicy(defaults.RootElement, 42_000, 15, null);

        using var overrides = ReadPolicy(@"C:\signal\ready", "0", "7", "15", "16", "-1", "abc", "1.5");
        var values = overrides.RootElement.EnumerateArray().ToArray();
        Assert.Equal(7, values.Length);
        AssertPolicy(values[0], 42_000, 0, @"C:\signal\ready");
        AssertPolicy(values[1], 42_000, 7, @"C:\signal\ready");
        AssertPolicy(values[2], 42_000, 15, @"C:\signal\ready");
        foreach (var value in values[3..])
        {
            AssertPolicy(value, 42_000, 15, @"C:\signal\ready");
        }
    }

    private static JsonDocument ReadPolicy(string? signalPath, string? grace, params string[] extraGraceValues)
    {
        var root = MtpTestRunnerScriptTests.RepositoryRoot();
        var module = Path.Combine(root, "scripts", "MtpTestRunner.psm1").Replace("'", "''", StringComparison.Ordinal);
        var command = $"Import-Module '{module}' -Force; " +
            "$policies = @(); $policies += Get-MtpTimeoutPolicy -TestHostTimeoutSeconds 42; " +
            string.Concat(extraGraceValues.Select(value =>
                $"$env:MCG_MTP_TEST_GRACEFUL_EXIT_SECONDS = '{value}'; $policies += Get-MtpTimeoutPolicy -TestHostTimeoutSeconds 42; ")) +
            "$policies | ConvertTo-Json -Compress";
        var startInfo = new ProcessStartInfo("powershell")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        startInfo.Environment.Remove("MCG_MTP_TEST_TIMEOUT_SIGNAL_PATH");
        startInfo.Environment.Remove("MCG_MTP_TEST_GRACEFUL_EXIT_SECONDS");
        if (signalPath is not null)
        {
            startInfo.Environment["MCG_MTP_TEST_TIMEOUT_SIGNAL_PATH"] = signalPath;
        }
        if (grace is not null)
        {
            startInfo.Environment["MCG_MTP_TEST_GRACEFUL_EXIT_SECONDS"] = grace;
        }
        var result = MtpTestRunnerScriptTests.Run(startInfo);
        Xunit.Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        return JsonDocument.Parse(result.Stdout.Trim());
    }

    private static void AssertPolicy(JsonElement policy, int timeoutMilliseconds, int gracefulExitSeconds, string? signalPath)
    {
        Xunit.Assert.Equal(timeoutMilliseconds, policy.GetProperty("TimeoutMilliseconds").GetInt32());
        Xunit.Assert.Equal(gracefulExitSeconds, policy.GetProperty("GracefulExitSeconds").GetInt32());
        Xunit.Assert.Equal(signalPath, policy.GetProperty("SignalPath").GetString());
    }
}

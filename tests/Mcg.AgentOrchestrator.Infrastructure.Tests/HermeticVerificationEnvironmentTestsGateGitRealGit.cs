using System.Collections;
using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class HermeticVerificationEnvironmentTestsGateGitRealGit : GoalAcceptanceVerifierTestBase
{
    [Xunit.Fact]
    public async Task RealGitReadsOnlyThePinnedGlobalConfigAndLocalOverridesIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-git-config-real-git", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry pair in Environment.GetEnvironmentVariables())
            {
                environment[(string)pair.Key] = (string?)pair.Value;
            }
            GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(environment, root);

            var init = await RunGitAsync(root, environment, "init");
            Assert.Equal(0, init.ExitCode);
            Assert.DoesNotContain("hint:", init.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("defaultBranch", init.Stderr, StringComparison.OrdinalIgnoreCase);
            var branch = await RunGitAsync(root, environment, "symbolic-ref", "HEAD");
            Assert.Equal(0, branch.ExitCode);
            Assert.Equal("refs/heads/master", branch.Stdout.Trim());

            var autocrlf = await RunGitAsync(root, environment, "config", "--null", "--show-origin", "--get", "core.autocrlf");
            Assert.Equal(0, autocrlf.ExitCode);
            Assert.Equal($"file:{environment["GIT_CONFIG_GLOBAL"]}\0true\0", autocrlf.Stdout);

            var helper = await RunGitAsync(root, environment, "config", "--get-all", "credential.helper");
            Assert.Equal(1, helper.ExitCode);
            Assert.Empty(helper.Stdout);
            var lfs = await RunGitAsync(root, environment, "config", "--get-regexp", "^filter\\.lfs\\.");
            Assert.Equal(1, lfs.ExitCode);
            Assert.Empty(lfs.Stdout);

            var gateConfigPath = environment["GIT_CONFIG_GLOBAL"]!;
            var pinnedContent = File.ReadAllBytes(gateConfigPath);
            Assert.True(Directory.Exists(gateConfigPath + ".lock"));
            var globalWrite = await RunGitAsync(root, environment, "config", "--global", "core.autocrlf", "false");
            Assert.NotEqual(0, globalWrite.ExitCode);
            Assert.Equal(pinnedContent, File.ReadAllBytes(gateConfigPath));

            var localWrite = await RunGitAsync(root, environment, "config", "core.autocrlf", "false");
            Assert.Equal(0, localWrite.ExitCode);
            var local = await RunGitAsync(root, environment, "config", "--show-origin", "--get", "core.autocrlf");
            Assert.Equal(0, local.ExitCode);
            Assert.Contains(".git/config", local.Stdout.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("\tfalse", local.Stdout.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string directory,
        IDictionary<string, string?> environment,
        params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment.Clear();
        foreach (var pair in environment)
        {
            if (pair.Value is not null)
            {
                start.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }
}

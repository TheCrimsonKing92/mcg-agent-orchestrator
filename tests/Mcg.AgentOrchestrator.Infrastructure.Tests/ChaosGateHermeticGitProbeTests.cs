using System.Diagnostics;
using Xunit;

public sealed class ChaosGateHermeticGitProbeTests : ChaosGateTestBase
{
    [Fact]
    public void SeededRepositoryIgnoresAmbientRepositoryAndSystemConfiguration()
    {
        var other = CreateTempDirectory();
        var initialized = InfrastructureTestSupport.RunGitProbe(other, ["init"]);
        Assert.True(initialized.Succeeded, initialized.ToString());
        var systemConfig = Path.Combine(other, "ambient-system.gitconfig");
        File.WriteAllText(systemConfig,
            "[init]\n\tdefaultBranch = ambient-system-branch\n[user]\n\temail = ambient-system@example.invalid\n");

        var inherited = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP" })
            inherited[name] = Environment.GetEnvironmentVariable(name);
        inherited["GIT_DIR"] = Path.Combine(other, ".git");
        inherited["GIT_CONFIG_SYSTEM"] = systemConfig;

        var captured = new List<Dictionary<string, string>>();
        string? intended = null;
        try
        {
            using (InfrastructureTestSupport.UseGitProbeSeam(inherited, process =>
                   {
                       captured.Add(process.StartInfo.Environment.ToDictionary(
                           pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
                       return process.Start();
                   }))
            {
                intended = CreateSeededRepo();
            }

            var head = InfrastructureTestSupport.RunGitProbe(intended, ["log", "-1", "--format=%s|%ae"]);
            Assert.True(head.Succeeded, head.ToString());
            Assert.Equal("Seed|chaos-tests@example.com", head.StandardOutput.Trim());
            var otherHead = InfrastructureTestSupport.RunGitProbe(other, ["rev-parse", "--verify", "HEAD"]);
            Assert.NotEqual(0, otherHead.ExitCode);
            var branch = InfrastructureTestSupport.RunGitProbe(intended, ["branch", "--show-current"]);
            Assert.True(branch.Succeeded, branch.ToString());
            Assert.NotEqual("ambient-system-branch", branch.StandardOutput.Trim());

            Assert.NotEmpty(captured);
            Assert.All(captured, environment =>
            {
                Assert.Equal("1", environment["GIT_CONFIG_NOSYSTEM"]);
                Assert.Equal(OperatingSystem.IsWindows() ? "NUL" : "/dev/null", environment["GIT_CONFIG_GLOBAL"]);
                Assert.False(environment.ContainsKey("GIT_DIR"));
                Assert.False(environment.ContainsKey("GIT_CONFIG_SYSTEM"));
            });
        }
        finally
        {
            if (intended is not null && Directory.Exists(intended)) Directory.Delete(intended, recursive: true);
            if (Directory.Exists(other)) Directory.Delete(other, recursive: true);
        }
    }
}

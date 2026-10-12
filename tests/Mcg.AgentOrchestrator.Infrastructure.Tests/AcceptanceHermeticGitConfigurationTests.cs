using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Parallel-safe: filesystem tests own unique temporary roots; source checks are read-only.
public sealed class AcceptanceHermeticGitConfigurationTests
{
    [Fact]
    public void Configure_EmptyEnvironment_CreatesHermeticReadOnlyConfig()
    {
        using var profile = new ProfileFixture();
        var environment = new Dictionary<string, string?>();

        AcceptanceHermeticGitConfiguration.Configure(environment, profile.Root);

        Assert.Equal("1", environment["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("never", environment["GCM_INTERACTIVE"]);
        Assert.Equal(profile.ConfigPath, environment["GIT_CONFIG_GLOBAL"]);
        AssertConfigFile(profile);
        AssertOnlyConfigAndLockDirectory(profile);
    }

    [Fact]
    public void Configure_AmbientEnvironment_OverwritesGitConfiguration()
    {
        using var profile = new ProfileFixture();
        var environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_NOSYSTEM"] = "0",
            ["GIT_CONFIG_GLOBAL"] = @"C:\operator\.gitconfig",
            ["GIT_TERMINAL_PROMPT"] = "1",
            ["GCM_INTERACTIVE"] = "always"
        };

        AcceptanceHermeticGitConfiguration.Configure(environment, profile.Root);

        Assert.Equal("1", environment["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal(profile.ConfigPath, environment["GIT_CONFIG_GLOBAL"]);
        Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("never", environment["GCM_INTERACTIVE"]);
    }

    [Fact]
    public void EnsureConfigFile_StaleReadOnlyFile_ReplacesBytesWithoutTemporaryFiles()
    {
        using var profile = new ProfileFixture();
        Directory.CreateDirectory(profile.GitDirectory);
        File.WriteAllText(profile.ConfigPath, "stale");
        File.SetAttributes(profile.ConfigPath, File.GetAttributes(profile.ConfigPath) | FileAttributes.ReadOnly);

        Assert.Equal(profile.ConfigPath, AcceptanceHermeticGitConfiguration.EnsureConfigFile(profile.Root));

        AssertConfigFile(profile);
        AssertOnlyConfigAndLockDirectory(profile);
    }

    [Fact]
    public void EnsureConfigFile_CurrentFile_PreservesLastWriteTimeWithoutTemporaryFiles()
    {
        using var profile = new ProfileFixture();
        Assert.Equal(profile.ConfigPath, AcceptanceHermeticGitConfiguration.EnsureConfigFile(profile.Root));
        AssertConfigFile(profile);
        AssertOnlyConfigAndLockDirectory(profile);
        var sentinel = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetAttributes(profile.ConfigPath, File.GetAttributes(profile.ConfigPath) & ~FileAttributes.ReadOnly);
        File.SetLastWriteTimeUtc(profile.ConfigPath, sentinel);
        File.SetAttributes(profile.ConfigPath, File.GetAttributes(profile.ConfigPath) | FileAttributes.ReadOnly);
        Assert.Equal(sentinel, File.GetLastWriteTimeUtc(profile.ConfigPath));

        Assert.Equal(profile.ConfigPath, AcceptanceHermeticGitConfiguration.EnsureConfigFile(profile.Root));

        Assert.Equal(sentinel, File.GetLastWriteTimeUtc(profile.ConfigPath));
        AssertConfigFile(profile);
        AssertOnlyConfigAndLockDirectory(profile);
    }

    [Fact]
    public void Verifier_HermeticGitConfiguration_DelegatesToSeparateOwner()
    {
        var root = VerifiedRepositoryRoot.Find();
        var workspaces = Path.Combine(root, "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces");
        var verifier = File.ReadAllText(Path.Combine(workspaces, "GoalAcceptanceVerifier.cs"));
        Assert.Contains("AcceptanceHermeticGitConfiguration.Configure", verifier);
        Assert.False(File.Exists(Path.Combine(workspaces, "GoalAcceptanceVerifier.HermeticGitConfiguration.cs")));
        var owner = Path.GetFullPath(Path.Combine(workspaces, "AcceptanceHermeticGitConfiguration.cs"));
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !RepositorySourceInventory.IsExcludedRelativePath(Path.GetRelativePath(root, path)))
            .Where(path => !string.Equals(Path.GetFullPath(path), owner, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var declarations = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot()
                .DescendantNodes().OfType<MethodDeclarationSyntax>();
            Assert.DoesNotContain(declarations, declaration => declaration.Identifier.ValueText == "EnsureHermeticGateGitConfig");
        }
    }

    [Fact]
    public void Canary_HermeticGitConfigurationOwner_TriggersAcceptanceCanary()
    {
        Assert.True(PostLandingCanaryTrigger.Evaluate(
            ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceHermeticGitConfiguration.cs"]).ShouldRun);
    }

    private static void AssertConfigFile(ProfileFixture profile)
    {
        Assert.Equal(new UTF8Encoding(false).GetBytes(AcceptanceHermeticGitConfiguration.GateConfig),
            File.ReadAllBytes(profile.ConfigPath));
        Assert.True(File.GetAttributes(profile.ConfigPath).HasFlag(FileAttributes.ReadOnly));
    }

    private static void AssertOnlyConfigAndLockDirectory(ProfileFixture profile)
    {
        Assert.Equal(new[] { profile.ConfigPath }, Directory.GetFiles(profile.GitDirectory));
        Assert.Equal(new[] { profile.ConfigPath + ".lock" }, Directory.GetDirectories(profile.GitDirectory));
    }

    private sealed class ProfileFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "acceptance-hermetic-git-" + Guid.NewGuid().ToString("N"));
        internal string GitDirectory => Path.Combine(Root, "git");
        internal string ConfigPath => Path.Combine(GitDirectory, "gate-gitconfig");

        internal ProfileFixture() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            if (File.Exists(ConfigPath))
                File.SetAttributes(ConfigPath, File.GetAttributes(ConfigPath) & ~FileAttributes.ReadOnly);
            Directory.Delete(Root, recursive: true);
        }
    }
}

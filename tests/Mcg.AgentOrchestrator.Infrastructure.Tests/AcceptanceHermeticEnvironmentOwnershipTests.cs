using System.Reflection;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceHermeticEnvironmentOwnershipTests
{
    private const string ConfigureMethodName = "Configure" + "HermeticVerificationEnvironment";
    private const BindingFlags DeclaredMembers = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    [Fact]
    public void AllowList_KeepsItsMembership()
    {
        string[] allowed =
        [
            "PATH", "path", "SystemDrive", "ProgramData", "DOTNET_ROOT", "NUGET_HTTP_CACHE_PATH",
            "HOME", "GIT_COMMITTER_EMAIL", "GOCACHE", "MCG_ORCHESTRATOR_REPOSITORY_ROOT",
            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable
        ];
        foreach (var name in allowed)
        {
            Assert.True(AcceptanceHermeticEnvironment.IsHermeticVerificationEnvironmentVariable(name), name);
        }

        string[] excluded = ["MCG_WORKER_SANDBOX", "USERNAME", "OPENAI_API_KEY", "GIT_DIR"];
        foreach (var name in excluded)
        {
            Assert.False(AcceptanceHermeticEnvironment.IsHermeticVerificationEnvironmentVariable(name), name);
        }
    }

    [Fact]
    public void HermeticEnvironment_HasOneOwner()
    {
        string[] movedNames =
        [
            "IsHermeticVerificationEnvironmentVariable", "IsInheritedHermeticVerificationEnvironmentVariable",
            "ResolvePerUserFolders", "IsHermeticSystemLocationVariable", "HermeticSystemLocationVariables",
            "HermeticGitIdentityName", "HermeticGitIdentityEmail"
        ];
        var verifier = typeof(GoalAcceptanceVerifier);
        foreach (var name in movedNames)
        {
            Assert.Empty(verifier.GetMember(name, DeclaredMembers));
        }

        var forward = Assert.IsAssignableFrom<MethodInfo>(Assert.Single(verifier.GetMember(ConfigureMethodName, DeclaredMembers)));
        Assert.True(forward.IsPublic);
        Assert.True(forward.IsStatic);

        var owner = typeof(AcceptanceHermeticEnvironment);
        Assert.True(owner.IsAbstract);
        Assert.True(owner.IsSealed);
        foreach (var name in movedNames.Append(ConfigureMethodName))
        {
            Assert.Single(owner.GetMember(name, DeclaredMembers));
        }

        var configure = owner.GetMethod(ConfigureMethodName, DeclaredMembers)!;
        Assert.True(configure.IsPublic);
        Assert.True(configure.IsStatic);
        Assert.True(owner.GetMethod("ResolvePerUserFolders", DeclaredMembers)!.IsAssembly);
        Assert.True(owner.GetMethod("IsHermeticVerificationEnvironmentVariable", DeclaredMembers)!.IsAssembly);
        Assert.NotNull(verifier.GetField("LegacyOwnedStartNegativeControlVariable", DeclaredMembers));
        Assert.NotNull(verifier.GetField("AcceptanceAttemptTrxPrefixVariable", DeclaredMembers));
    }

    [Fact]
    public void SourceCallers_UseTheOwner()
    {
        var root = VerifiedRepositoryRoot.Find();
        var pattern = Regex.Escape(ConfigureMethodName) + @"[(,)]";
        var matches = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !RepositorySourceInventory.IsExcludedRelativePath(Path.GetRelativePath(root, path)))
            .SelectMany(path => File.ReadLines(path)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && Regex.IsMatch(line, pattern))
                .Select(line => (Path: Path.GetRelativePath(root, path).Replace('\\', '/'), Line: line)))
            .ToArray();
        var declarations = matches.Where(match => match.Line.Contains("static void " + ConfigureMethodName, StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, declarations.Length);

        var callers = matches.Except(declarations).ToArray();
        var ownerCall = "AcceptanceHermeticEnvironment." + ConfigureMethodName;
        var retainedCaller = Assert.Single(callers.Where(match => !match.Line.Contains(ownerCall, StringComparison.Ordinal)));
        Assert.Equal("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorParallelAcceptanceAttempts.cs", retainedCaller.Path);
        var migratedCallers = callers.Where(match => match.Line.Contains(ownerCall, StringComparison.Ordinal)).ToArray();
        Assert.Contains(migratedCallers, match => match.Path == "src/Mcg.AgentOrchestrator.App/Orchestration/PostLandingCanaryRunner.cs");
        Assert.Contains(migratedCallers, match => match.Path == "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceShellProcessStartInfo.cs");

        var workspaces = Path.Combine(root, "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces");
        Assert.False(File.Exists(Path.Combine(workspaces, "GoalAcceptanceVerifier.HermeticSystemLocations.cs")));
        Assert.DoesNotContain("partial class", File.ReadAllText(Path.Combine(workspaces, "AcceptanceHermeticEnvironment.cs")));
    }
}

using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class HermeticVerificationEnvironmentTests : GoalAcceptanceVerifierTestBase
{
    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_constructs_allow_list_env_so_the_gate_verdict_is_hermetic")]
    public void GoalAcceptanceVerifierConstructsAllowListEnvSoTheGateVerdictIsHermetic()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            // Handoff vars leak in from a handoff-spawned conductor and make CLI grandchildren skip
            // startup cleanup, which silently greened a real backlog-list regression on some runs.
            ["MCG_ORCHESTRATOR_HANDOFF_READY_PATH"] = @"C:\ready.txt",
            ["MCG_ORCHESTRATOR_HANDOFF_TOKEN"] = "token-1",
            ["mcg_orchestrator_handoff_incumbent_pid"] = "4242",
            [WorkerSandboxOptions.EnabledVariable] = "1",
            [WorkerSandboxOptions.AccountVariable] = "worker",
            // Outer-attempt identity: inheriting these lets a child overwrite genuine receipts and pass
            // as the SAME custodian, skipping the live-owner check that guards a recursive artifact delete.
            [GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable] = @"C:\outer\attempt-1",
            [AcceptanceAttemptArtifactCustody.AttemptIdVariable] = "attempt-1",
            [AcceptanceAttemptArtifactCustody.LivenessCheckHintVariable] = @"C:\outer\meta.json",
            // One-shot operator escape hatch that would otherwise wipe intact artifacts in every child.
            [DotnetBuildEnvironmentManager.ForceCleanStaleLeaseArtifactsVariable] = "1",
            ["MCG_ACCEPTANCE_FULL_SHARDS"] = "1",
            ["MCG_ACCEPTANCE_CHANGE_SCOPED"] = "0",
            ["MCG_ORCHESTRATOR_DISABLE_DISPATCH_START"] = "1",
            ["MCG_ORCHESTRATOR_TEST_REWRITE_REAL_WORKER_COMMANDS"] = "1",
            ["MCG_ORCHESTRATOR_PROTECTED_PID"] = "4242",
            ["MCG_DOTNET_ISOLATED_ROOT"] = @"C:\ambient-dotnet",
            ["MCG_BUILD_MAXCPUCOUNT"] = "1",
            ["OPENAI_API_KEY"] = "real-secret",
            ["ANTHROPIC_API_KEY"] = "real-secret",
            ["DISCORD_BOT_TOKEN"] = "real-secret",
            // Runtime-discovery inputs the child genuinely needs survive the allow-list.
            ["PATH"] = @"C:\windows",
            ["TEMP"] = @"C:\temp",
            ["DOTNET_ROOT"] = @"C:\dotnet",
            ["NUGET_PACKAGES"] = @"C:\packages"
        };

        var repositoryRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mcg-hermetic-repo"));
        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(environment, repositoryRoot);

        Assert.DoesNotContain(environment.Keys, name => name.StartsWith("MCG_", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("MCG_ORCHESTRATOR_REPOSITORY_ROOT", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("OPENAI_API_KEY", environment.Keys);
        Assert.DoesNotContain("ANTHROPIC_API_KEY", environment.Keys);
        Assert.DoesNotContain("DISCORD_BOT_TOKEN", environment.Keys);
        Assert.Equal(repositoryRoot, environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"]);
        Assert.Equal(@"C:\windows", environment["PATH"]);
        Assert.Equal(@"C:\temp", environment["TEMP"]);
        Assert.Equal(@"C:\dotnet", environment["DOTNET_ROOT"]);
        Assert.Equal(@"C:\packages", environment["NUGET_PACKAGES"]);
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "mcg-hvp"),
            environment["USERPROFILE"]);
        Assert.Equal(environment["USERPROFILE"], environment["DOTNET_CLI_HOME"]);

        // Relocating DOTNET_CLI_HOME makes the SDK treat the run as a first use and PERSIST
        // "$DOTNET_CLI_HOME\.dotnet\tools" into HKCU\Environment\Path - ambient state escaping the
        // hermetic boundary, one dead entry at a time, invisible until PATH is inspected by hand.
        // DOTNET_SKIP_FIRST_TIME_EXPERIENCE does not suppress it on .NET 10; only this variable does.
        Assert.Equal("0", environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"]);
        Assert.Equal("1", environment["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("0", environment["DOTNET_CLI_USE_MSBUILD_SERVER"]);
        Assert.DoesNotContain("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", environment.Keys);
        if (OperatingSystem.IsWindows())
        {
            var expectedCache = Path.Combine(environment["USERPROFILE"]!, "powershell", "ModuleAnalysisCache");
            Assert.Equal(expectedCache, environment["PSModuleAnalysisCachePath"]);
            Assert.True(Directory.Exists(Path.GetDirectoryName(expectedCache)!));
        }
        Assert.All(environment.Keys, name => Assert.True(
            GoalAcceptanceVerifier.IsHermeticVerificationEnvironmentVariable(name),
            $"Unexpected verification environment variable survived: {name}"));
        Assert.True(GoalAcceptanceVerifier.IsHermeticVerificationEnvironmentVariable(
            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable));
    }

    [Xunit.Fact(DisplayName = "Hermetic_verification_scopes_the_NuGet_HTTP_cache_to_the_build_environment")]
    public void HermeticVerificationScopesTheNuGetHttpCacheToTheBuildEnvironment()
    {
        var buildEnvironmentRoot = Path.Combine(
            Path.GetTempPath(),
            "mcg-hermetic-nuget-cache-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(Path.GetTempPath(), "shared-nuget-http-cache")
            };

            GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(
                environment,
                Path.GetTempPath(),
                buildEnvironmentRoot);

            var expected = Path.Combine(buildEnvironmentRoot, "nuget-http-cache");
            Assert.Equal(expected, environment["NUGET_HTTP_CACHE_PATH"]);
            Assert.True(Directory.Exists(expected));
        }
        finally
        {
            if (Directory.Exists(buildEnvironmentRoot))
            {
                Directory.Delete(buildEnvironmentRoot, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "Hermetic_verification_keeps_the_build_system_file_full_solution_rule_observable")]
    public async Task HermeticVerificationKeepsTheBuildSystemFileFullSolutionRuleObservable()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-hermetic-rule-tests", Guid.NewGuid().ToString("N"));
        var manifestDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(manifestDirectory);
        File.WriteAllText(
            Path.Combine(manifestDirectory, "acceptance-manifest.json"),
            AcceptanceManifestTestDefaults.WithEngine("""
                {
                  "version": 1,
                  "checks": [
                    { "name": "git diff whitespace", "type": "command", "command": "git", "arguments": ["diff", "--check"] },
                    { "name": "core tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                    { "name": "infrastructure tests", "type": "dotnet-test", "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "arguments": ["--verbosity", "minimal"] },
                    { "name": "full dotnet tests", "type": "dotnet-test", "project": "Mcg.AgentOrchestrator.sln", "arguments": ["--verbosity", "minimal"] }
                  ],
                  "forbiddenChangedPathGlobs": []
                }
                """));
        try
        {
            var calls = new List<string[]>();
            var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
                new(0, ""),
                new(0, ""),
                new(0, "Full tests passed.")
            ]);
            var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            {
                calls.Add(args);
                return Task.FromResult(responses.Dequeue());
            });

            var result = await verifier.RunAsync(root, changedFiles: ["Directory.Build.props"]);

            Assert.True(result.Passed);
            Assert.Contains("Mcg.AgentOrchestrator.sln", calls[2], StringComparer.OrdinalIgnoreCase);
            Assert.Contains(result.Checks!, check => check.Name == "full dotnet tests");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}

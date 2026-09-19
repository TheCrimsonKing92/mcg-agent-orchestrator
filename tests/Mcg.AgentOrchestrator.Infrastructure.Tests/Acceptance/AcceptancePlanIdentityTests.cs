using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

namespace Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests;

public sealed class AcceptancePlanIdentityTests
{
    [Fact]
    public void Effective_plan_identity_uses_loaded_engine_settings()
    {
        var root = CreateWorkspace("""
            {
              "version": 1,
              "engine": { "maxConcurrentShards": 2 },
              "checks": [{
                "name": "git diff whitespace",
                "type": "command",
                "command": "git",
                "arguments": ["diff", "--check"]
              }]
            }
            """);
        try
        {
            var settings = AcceptanceGateEngineSettings.Load(root);
            var checks = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(root);
            var identityMethod = typeof(GoalAcceptanceVerifier).GetMethod(
                "ComputeEffectiveAcceptanceManifestIdentity",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var expectedInGateIdentity = (string)identityMethod.Invoke(null, [checks, settings])!;
            var defaultSettingsIdentity = (string)identityMethod.Invoke(
                null,
                [checks, new AcceptanceGateEngineSettings()])!;

            var effectivePlanIdentity = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(root);

            Assert.Equal(expectedInGateIdentity, effectivePlanIdentity);
            Assert.NotEqual(defaultSettingsIdentity, effectivePlanIdentity);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateWorkspace(string manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-acceptance-plan-identity-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "acceptance-manifest.json"), manifest);
        return root;
    }
}

using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: in-memory profile selection and command hashing only.
public sealed class ProgressiveReviewGlanceLunaSelectionTests
{
    [Fact]
    public void DefaultProfiles_SelectSparkProfileWithLunaModel()
    {
        var selection = SubscriptionCliProgressiveReviewGlanceRunner.SelectProfile(WorkerProfileCatalog.Default());

        Assert.Equal("codex-luna", selection.ProfileName);
        Assert.Equal(AgentCatalog.OpenAiGpt6LunaSubscriptionModelAlias, selection.ModelAlias);
    }

    [Fact]
    public void CodexCliOnlyCatalog_PreservesFallbackModel()
    {
        var profiles = new WorkerProfileCatalog([WorkerProfileCatalog.Default().GetRequired("codex-cli")]);

        var selection = SubscriptionCliProgressiveReviewGlanceRunner.SelectProfile(profiles);

        Assert.Equal("codex-cli", selection.ProfileName);
        Assert.Equal(AgentCatalog.OpenAiSubscriptionModelAlias, selection.ModelAlias);
    }

    [Fact]
    public void LunaContractIdentity_ChangesOldSparkCommandFingerprint()
    {
        var profiles = WorkerProfileCatalog.Default();
        var identity = new SubscriptionCliProgressiveReviewGlanceRunner(profiles).GetContractIdentity();
        var commandContract = string.Join("\n", profiles.GetRequired("codex-spark").CommandTemplate,
            "gpt-5.3-codex-spark", AgentCatalog.RoutineSubscriptionReasoningEffort);
        var oldFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(commandContract)));

        Assert.Equal("codex-luna", identity.Profile);
        Assert.Equal(AgentCatalog.OpenAiGpt6LunaSubscriptionModelAlias, identity.Model);
        Assert.NotEqual(oldFingerprint, identity.CommandFingerprint);
    }
}

using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ModelFunctionCatalogStoreTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void RetirementNormalizesPrimaryAndBackupBindingsWithoutChangingIdentity(bool backup)
    {
        var path = Path.Combine(CreateTempDirectory(), "model-functions.json");
        var binding = new ModelFunctionBinding("custom-purpose", ModelLane.Capable,
            new ModelProfile("oPeNaI", "GpT-6-SoL", ModelCapability.Text | ModelCapability.ToolUse, SubscriptionMode.ApiKey, "xhigh", 321),
            "Custom name", new SubscriptionLaunchProfile("custom-profile", "GPT-6-SOL", "low"));
        var control = binding with
        {
            Purpose = "other-purpose", Lane = ModelLane.CheapApi,
            Model = binding.Model with { ProviderName = "Anthropic" }
        };
        ModelFunctionCatalogStore.Save(path, new ModelFunctionCatalog([binding, control]));
        if (backup)
        {
            ModelFunctionCatalogStore.Save(path, ModelFunctionCatalog.Empty);
            File.WriteAllText(path, "{corrupt}");
        }
        var primaryText = File.ReadAllText(path);
        var backupText = backup ? File.ReadAllText(path + ".bak") : null;
        var restored = ModelFunctionCatalogStore.Load(path);
        Assert.Equal(binding with
        {
            Model = binding.Model with { ModelName = "gpt-6.1-sol" },
            Subscription = binding.Subscription! with { ModelAlias = "gpt-6.1-sol" }
        }, restored.Bindings[0]);
        Assert.Equal(control, restored.Bindings[1]);
        Assert.Equal(primaryText, File.ReadAllText(path));
        if (backup) Assert.Equal(backupText, File.ReadAllText(path + ".bak"));
    }

    [Xunit.Theory]
    [Xunit.InlineData("gpt-6-luna")]
    [Xunit.InlineData("gpt-6-astra")]
    [Xunit.InlineData("custom-gpt-6-sol")]
    [Xunit.InlineData("gpt-6-sol ")]
    [Xunit.InlineData(null)]
    public void RetirementPreservesOtherInternalAliases(string? alias)
    {
        var path = Path.Combine(CreateTempDirectory(), "model-functions.json");
        var binding = new ModelFunctionBinding("custom-purpose", ModelLane.Local,
            new ModelProfile("OpenAI", alias ?? "api-custom", ModelCapability.Code, SubscriptionMode.ApiKey),
            Subscription: new SubscriptionLaunchProfile("codex-cli", alias));
        ModelFunctionCatalogStore.Save(path, new ModelFunctionCatalog([binding]));
        Assert.Equal(binding, Assert.Single(ModelFunctionCatalogStore.Load(path).Bindings));
    }

    [Xunit.Fact]
    public void ParsedEmptyPrimaryAndCorruptBackupKeepExistingEmptyBehavior()
    {
        var path = Path.Combine(CreateTempDirectory(), "model-functions.json");
        ModelFunctionCatalogStore.Save(path, new ModelFunctionCatalog([
            new ModelFunctionBinding("purpose", ModelLane.Capable,
                new ModelProfile("OpenAI", "gpt-6-sol", ModelCapability.Text, SubscriptionMode.ApiKey))]));
        ModelFunctionCatalogStore.Save(path, ModelFunctionCatalog.Empty);
        Assert.Empty(ModelFunctionCatalogStore.Load(path).Bindings);
        File.WriteAllText(path, "{corrupt}");
        File.WriteAllText(path + ".bak", "{also-corrupt}");
        Assert.Empty(ModelFunctionCatalogStore.Load(path).Bindings);
    }

    [Xunit.Fact(DisplayName = "ModelFunctionCatalogStore_roundtrips_bindings")]
    public void ModelFunctionCatalogStoreRoundtripsBindings()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "model-functions.json");
    var catalog = new ModelFunctionCatalog(
    [
        new ModelFunctionBinding(
            ModelFunctionPurposes.AcceptanceJudge,
            ModelLane.CheapApi,
            new ModelProfile("Anthropic", "claude-haiku-4-5", ModelCapability.Text | ModelCapability.Code, SubscriptionMode.ApiKey))
    ]);

    ModelFunctionCatalogStore.Save(path, catalog);
    var restored = ModelFunctionCatalogStore.Load(path);

    Assert.Equal(1, restored.Bindings.Count);
    Assert.Equal(ModelFunctionPurposes.AcceptanceJudge, restored.Bindings[0].Purpose);
    Assert.Equal("claude-haiku-4-5", restored.Bindings[0].Model.ModelName);
}
    [Xunit.Fact(DisplayName = "ModelFunctionCatalogStore_absent_file_returns_empty")]
    public void ModelFunctionCatalogStoreAbsentFileReturnsEmpty()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "model-functions.json");

    var catalog = ModelFunctionCatalogStore.Load(path);

    Assert.Equal(0, catalog.Bindings.Count);
}
    [Xunit.Fact(DisplayName = "ModelFunctionCatalogStore_save_leaves_no_tmp_file")]
    public void ModelFunctionCatalogStoreSaveLeavesNoTmpFile()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "model-functions.json");
    var catalog = new ModelFunctionCatalog(
    [
        new ModelFunctionBinding(
            ModelFunctionPurposes.AcceptanceJudge,
            ModelLane.CheapApi,
            new ModelProfile("Anthropic", "claude-haiku-4-5", ModelCapability.Text, SubscriptionMode.ApiKey))
    ]);

    ModelFunctionCatalogStore.Save(path, catalog);

    Assert.False(File.Exists(path + ".tmp"));
    Assert.True(File.Exists(path));
}
    [Xunit.Fact(DisplayName = "ModelFunctionCatalogStore_corrupt_file_recovers_from_bak")]
    public void ModelFunctionCatalogStoreCorruptFileRecoversFromBak()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "model-functions.json");
    var firstCatalog = new ModelFunctionCatalog(
    [
        new ModelFunctionBinding(
            ModelFunctionPurposes.AcceptanceJudge,
            ModelLane.CheapApi,
            new ModelProfile("Anthropic", "claude-bak-model", ModelCapability.Text, SubscriptionMode.ApiKey))
    ]);
    var secondCatalog = new ModelFunctionCatalog(
    [
        new ModelFunctionBinding(
            ModelFunctionPurposes.AcceptanceJudge,
            ModelLane.Capable,
            new ModelProfile("Anthropic", "claude-new-model", ModelCapability.Text, SubscriptionMode.ApiKey))
    ]);

    // First save writes path; second save moves path → .bak and writes new content
    ModelFunctionCatalogStore.Save(path, firstCatalog);
    ModelFunctionCatalogStore.Save(path, secondCatalog);
    File.WriteAllText(path, "{{corrupt}}");

    var recovered = ModelFunctionCatalogStore.Load(path);

    Assert.Equal(1, recovered.Bindings.Count);
    Assert.Equal("claude-bak-model", recovered.Bindings[0].Model.ModelName);
}
    [Xunit.Fact(DisplayName = "ModelFunctionCatalogStore_corrupt_file_without_bak_returns_empty")]
    public void ModelFunctionCatalogStoreCorruptFileWithoutBakReturnsEmpty()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "model-functions.json");
    File.WriteAllText(path, "{{corrupt}}");

    var catalog = ModelFunctionCatalogStore.Load(path);

    Assert.Equal(0, catalog.Bindings.Count);
}
}

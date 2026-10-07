using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ModelFunctionCatalogStoreTests
{
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

using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

[Xunit.Collection("EnvMutation")]
public sealed class WorkerProfileTests
{
    [Xunit.Fact(DisplayName = "WorkerProfileCatalog_default_contains_local_subscription_bridges")]
    public void WorkerProfileCatalogDefaultContainsLocalSubscriptionBridges()
{
    var catalog = WorkerProfileCatalog.Default();
    var profile = catalog.GetRequired("LOCAL-ECHO");
    var codex = catalog.GetRequired("codex-cli");
    var claude = catalog.GetRequired("claude-cli");
    var claudeProvider = WorkerProviderCatalog.Default().ResolveProfile(claude.Name);

    Assert.Equal("local-echo", profile.Name);
    Assert.Contains("{promptPath}", profile.CommandTemplate, StringComparison.Ordinal);
    Assert.Contains("codex exec", codex.CommandTemplate, StringComparison.Ordinal);
    Assert.Contains("--skip-git-repo-check", codex.CommandTemplate, StringComparison.Ordinal);
    Assert.Contains("--model {subscriptionModelName}", codex.CommandTemplate, StringComparison.Ordinal);
    Assert.Contains("-c model_reasoning_effort={subscriptionReasoningEffort}", codex.CommandTemplate, StringComparison.Ordinal);
    Assert.Contains("--sandbox {sandboxMode}", codex.CommandTemplate, StringComparison.Ordinal);
    Assert.Contains("--cd {workingDirectory}", codex.CommandTemplate, StringComparison.Ordinal);
    Assert.DoesNotContain("{promptPath}", codex.CommandTemplate, StringComparison.Ordinal);
    Assert.Contains("claude --model {subscriptionModelName}", claude.CommandTemplate, StringComparison.Ordinal);
    Assert.Contains("--permission-mode {permissionMode}", claude.CommandTemplate, StringComparison.Ordinal);
    Assert.DoesNotContain(" -p", claude.CommandTemplate, StringComparison.Ordinal);
    Assert.DoesNotContain("{promptPath}", claude.CommandTemplate, StringComparison.Ordinal);
    Assert.True(WorkerProfileDiagnostics.EvaluatePatchCapability(claude, claudeProvider).IsPatchCapable);
}

    [Xunit.Fact(DisplayName = "Claude_launcher_without_permission_mode_is_not_patch_capable")]
    public void ClaudeLauncherWithoutPermissionModeIsNotPatchCapable()
{
    var provider = WorkerProviderCatalog.Default().Resolve(ProviderKind.AnthropicClaudeCli);
    var capability = WorkerProfileDiagnostics.EvaluatePatchCapability(
        new WorkerProfile("test-claude", "claude --model {subscriptionModelName} -p (Get-Content -Raw {promptPath})"),
        provider);

    Assert.False(capability.IsPatchCapable);
    Assert.Contains("--permission-mode", capability.Detail, StringComparison.Ordinal);

    Assert.True(WorkerProfileDiagnostics.EvaluatePatchCapability(
        new WorkerProfile("test-claude", "claude --model {subscriptionModelName} --permission-mode acceptEdits"),
        provider).IsPatchCapable);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDiagnostics_validates_real_claude_launcher")]
    public void WorkerProfileDiagnosticsValidatesRealClaudeLauncher()
{
    var provider = WorkerProviderCatalog.Default().Resolve(ProviderKind.AnthropicClaudeCli);
    var profile = new WorkerProfile("claude-cli", "claude --model {subscriptionModelName} --permission-mode {permissionMode}");

    var validation = WorkerProfileDiagnostics.EvaluateRealLauncher(profile, provider, executable => executable == "claude");

    Assert.True(validation.IsRealLauncher);
    Assert.Equal("claude", validation.Executable);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDiagnostics_rejects_echo_stub_claude_launcher")]
    public void WorkerProfileDiagnosticsRejectsEchoStubClaudeLauncher()
{
    var provider = WorkerProviderCatalog.Default().Resolve(ProviderKind.AnthropicClaudeCli);
    var profile = new WorkerProfile("claude-cli", "Write-Output {subscriptionModelName}");

    var validation = WorkerProfileDiagnostics.EvaluateRealLauncher(profile, provider, _ => true);

    Assert.False(validation.IsRealLauncher);
    Assert.Equal("Write-Output", validation.Executable);
    Assert.Contains("not the expected claude CLI", validation.Detail, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileCatalog_default_codex_oss_profile_is_patch_capable_local_bridge")]
    public void WorkerProfileCatalogDefaultCodexOssProfileIsPatchCapableLocalBridge()
{
    var codexOss = WorkerProfileCatalog.Default().GetRequired("codex-oss-cli");

    Assert.Contains("--oss --local-provider ollama", codexOss.CommandTemplate, StringComparison.Ordinal);
    Assert.Contains("--model {subscriptionModelName}", codexOss.CommandTemplate, StringComparison.Ordinal);
    Assert.True(WorkerProfileDiagnostics.EvaluatePatchCapability(codexOss.CommandTemplate).IsPatchCapable);
}

    [Xunit.Fact(DisplayName = "Ollama_agents_default_to_qwen_code_subscription_profile")]
    public void OllamaAgentsDefaultToQwenCodeSubscriptionProfile()
{
    var agent = new AgentDefinition(
        AgentId.New(),
        "Local developer",
        AgentRole.Developer,
        new ModelProfile("Ollama", "qwen3:8b", ModelCapability.Text | ModelCapability.Code, SubscriptionMode.LocalBridge),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription);

    Assert.Equal("qwen-code-cli", WorkerProfileDispatcher.ResolveSubscriptionProfileName(agent));
}

    [Xunit.Fact(DisplayName = "WorkerProfileCatalog_default_qwen_code_profile_pins_model_and_prompt")]
    public void WorkerProfileCatalogDefaultQwenCodeProfilePinsModelAndPrompt()
{
    var qwenCode = WorkerProfileCatalog.Default().GetRequired("qwen-code-cli");

    Assert.Contains("OPENAI_MODEL={subscriptionModelName}", qwenCode.CommandTemplate, StringComparison.Ordinal);
    Assert.Contains("Set-Location {workingDirectory}", qwenCode.CommandTemplate, StringComparison.Ordinal);
    Assert.Contains("Get-Content -Raw {promptPath}", qwenCode.CommandTemplate, StringComparison.Ordinal);
    Assert.Contains("127.0.0.1:11434", qwenCode.CommandTemplate, StringComparison.Ordinal);
    Assert.False(WorkerProfileDiagnostics.IsEchoOnlyCommand(qwenCode.CommandTemplate));
}
    [Xunit.Fact(DisplayName = "WorkerProfileCatalog_upsert_replaces_existing_profile")]
    public void WorkerProfileCatalogUpsertReplacesExistingProfile()
{
    var defaults = WorkerProfileCatalog.Default();
    var catalog = defaults
        .Upsert(new WorkerProfile("local-echo", "Get-Content {promptPath}"));

    Assert.Equal(defaults.Profiles.Count, catalog.Profiles.Count);
    Assert.Equal("Get-Content {promptPath}", catalog.GetRequired("local-echo").CommandTemplate);
}
    [Xunit.Fact(DisplayName = "WorkerProfileCatalog_merge_upserts_imported_profiles")]
    public void WorkerProfileCatalogMergeUpsertsImportedProfiles()
{
    var defaults = WorkerProfileCatalog.Default();
    var current = defaults
        .Upsert(new WorkerProfile("codex", "codex exec {promptPath}"));
    var imported = new WorkerProfileCatalog(
    [
        new WorkerProfile("codex", "codex exec --full-auto {promptPath}"),
        new WorkerProfile("claude", "claude --file {promptPath}")
    ]);

    var merged = current.Merge(imported);

    Assert.Equal(defaults.Profiles.Count + 2, merged.Profiles.Count);
    Assert.Equal("codex exec --full-auto {promptPath}", merged.GetRequired("codex").CommandTemplate);
    Assert.Equal("claude --file {promptPath}", merged.GetRequired("CLAUDE").CommandTemplate);
}
    [Xunit.Fact(DisplayName = "WorkerProfileStore_roundtrips_profiles")]
    public void WorkerProfileStoreRoundtripsProfiles()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "workers.json");
    var catalog = WorkerProfileCatalog.Default().Upsert(new WorkerProfile("agent", "agent-cli --file {promptPath}"));

    WorkerProfileStore.Save(path, catalog);
    var restored = WorkerProfileStore.Load(path);

    Assert.Equal("agent-cli --file {promptPath}", restored.GetRequired("agent").CommandTemplate);

    var previousDirectory = Environment.CurrentDirectory;
    try
    {
        Environment.CurrentDirectory = root;
        WorkerProfileStore.Save("workers-local.json", catalog);
        Assert.True(File.Exists(Path.Combine(root, "workers-local.json")));
    }
    finally
    {
        Environment.CurrentDirectory = previousDirectory;
    }
}
    [Xunit.Fact(DisplayName = "WorkerProfileStore_load_merges_saved_profiles_with_new_defaults")]
    public void WorkerProfileStoreLoadMergesSavedProfilesWithNewDefaults()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "workers.json");
    var saved = new WorkerProfileCatalog(
    [
        new WorkerProfile("local-echo", "Get-Content {promptPath}"),
        new WorkerProfile("custom-agent", "custom-agent --prompt {promptPath}")
    ]);

    WorkerProfileStore.Save(path, saved);
    var restored = WorkerProfileStore.Load(path);

    Assert.Equal("Get-Content {promptPath}", restored.GetRequired("local-echo").CommandTemplate);
    Assert.Equal("custom-agent --prompt {promptPath}", restored.GetRequired("custom-agent").CommandTemplate);
    Assert.Contains("codex exec", restored.GetRequired("codex-cli").CommandTemplate, StringComparison.Ordinal);
    Assert.Contains("--model {subscriptionModelName}", restored.GetRequired("codex-cli").CommandTemplate, StringComparison.Ordinal);
    Assert.Contains("-c model_reasoning_effort={subscriptionReasoningEffort}", restored.GetRequired("codex-cli").CommandTemplate, StringComparison.Ordinal);
    Assert.Contains("claude --model {subscriptionModelName} --permission-mode {permissionMode}", restored.GetRequired("claude-cli").CommandTemplate, StringComparison.Ordinal);
    Assert.DoesNotContain("{promptPath}", restored.GetRequired("claude-cli").CommandTemplate, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "WorkerProfileStore_load_repairs_stale_default_subscription_profiles")]
    public void WorkerProfileStoreLoadRepairsStaleDefaultSubscriptionProfiles()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "workers.json");
    var saved = WorkerProfileCatalog.Default()
        .Upsert(new WorkerProfile("claude-cli", "claude -p (Get-Content -Raw {promptPath})"));

    WorkerProfileStore.Save(path, saved);
    var restored = WorkerProfileStore.Load(path);

    Assert.Contains("--model {subscriptionModelName}", restored.GetRequired("claude-cli").CommandTemplate, StringComparison.Ordinal);
    Assert.DoesNotContain("{promptPath}", restored.GetRequired("claude-cli").CommandTemplate, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "WorkerProfileStore_load_repairs_claude_profile_missing_permission_mode")]
    public void WorkerProfileStoreLoadRepairsClaudeProfileMissingPermissionMode()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "workers.json");
    var saved = WorkerProfileCatalog.Default()
        .Upsert(new WorkerProfile("claude-cli", "claude --model {subscriptionModelName} -p (Get-Content -Raw {promptPath})"));

    WorkerProfileStore.Save(path, saved);
    var restored = WorkerProfileStore.Load(path);

    Assert.Contains("--permission-mode {permissionMode}", restored.GetRequired("claude-cli").CommandTemplate, StringComparison.Ordinal);
    Assert.True(WorkerProfileDiagnostics.EvaluatePatchCapability(restored.GetRequired("claude-cli").CommandTemplate).IsPatchCapable);
}
    [Xunit.Fact(DisplayName = "WorkerProfileStore_save_leaves_no_tmp_file")]
    public void WorkerProfileStoreSaveLeavesNoTmpFile()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "workers.json");

    WorkerProfileStore.Save(path, WorkerProfileCatalog.Default());

    Assert.False(File.Exists(path + ".tmp"));
    Assert.True(File.Exists(path));
}
    [Xunit.Fact(DisplayName = "WorkerProfileStore_corrupt_file_recovers_from_bak")]
    public void WorkerProfileStoreCorruptFileRecoversFromBak()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "workers.json");
    var firstCatalog = new WorkerProfileCatalog([new WorkerProfile("bak-agent", "bak-agent-cli {promptPath}")]);

    // First save writes path; second save moves path → .bak and writes new content
    WorkerProfileStore.Save(path, firstCatalog);
    WorkerProfileStore.Save(path, WorkerProfileCatalog.Default());
    File.WriteAllText(path, "{{corrupt}}");

    var recovered = WorkerProfileStore.Load(path);

    Assert.Equal("bak-agent-cli {promptPath}", recovered.GetRequired("bak-agent").CommandTemplate);
}
    [Xunit.Fact(DisplayName = "WorkerProfileStore_corrupt_file_without_bak_falls_back_to_defaults")]
    public void WorkerProfileStoreCorruptFileWithoutBakFallsBackToDefaults()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "workers.json");
    File.WriteAllText(path, "{{corrupt}}");

    var catalog = WorkerProfileStore.Load(path);

    Assert.Contains("codex exec", catalog.GetRequired("codex-cli").CommandTemplate, StringComparison.Ordinal);
}
    [Xunit.Fact(DisplayName = "WorkerProfileStore_load_required_corrupt_file_recovers_from_bak")]
    public void WorkerProfileStoreLoadRequiredCorruptFileRecoversFromBak()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "workers.json");
    var firstCatalog = new WorkerProfileCatalog([new WorkerProfile("bak-agent", "bak-agent-cli {promptPath}")]);

    WorkerProfileStore.Save(path, firstCatalog);
    WorkerProfileStore.Save(path, new WorkerProfileCatalog([new WorkerProfile("other", "other-cli {promptPath}")]));
    File.WriteAllText(path, "{{corrupt}}");

    var recovered = WorkerProfileStore.LoadRequired(path);

    Assert.Equal("bak-agent-cli {promptPath}", recovered.GetRequired("bak-agent").CommandTemplate);
}
    [Xunit.Fact(DisplayName = "WorkerProfileStore_load_required_corrupt_without_bak_throws")]
    public void WorkerProfileStoreLoadRequiredCorruptWithoutBakThrows()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "workers.json");
    File.WriteAllText(path, "{{corrupt}}");

    Assert.ThrowsAny<InvalidDataException>(() => WorkerProfileStore.LoadRequired(path));
}
    [Xunit.Fact(DisplayName = "WorkerProfileStore_load_required_rejects_missing_or_empty_files")]
    public void WorkerProfileStoreLoadRequiredRejectsMissingOrEmptyFiles()
{
    var root = CreateTempDirectory();
    var missing = Path.Combine(root, "missing.json");
    var empty = Path.Combine(root, "empty.json");
    File.WriteAllText(empty, "{\"profiles\":[]}");

    Assert.ThrowsAny<FileNotFoundException>(() => WorkerProfileStore.LoadRequired(missing));
    Assert.ThrowsAny<InvalidDataException>(() => WorkerProfileStore.LoadRequired(empty));
}
    [Xunit.Fact(DisplayName = "WorkerProfileStore_load_repairs_codex_profile_with_hardcoded_sandbox")]
    public void WorkerProfileStoreLoadRepairsCodexProfileWithHardcodedSandbox()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "workers.json");
    var saved = WorkerProfileCatalog.Default()
        .Upsert(new WorkerProfile("codex-cli", "codex exec --skip-git-repo-check --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})"));

    WorkerProfileStore.Save(path, saved);
    var restored = WorkerProfileStore.Load(path);

    Assert.Contains("--sandbox {sandboxMode}", restored.GetRequired("codex-cli").CommandTemplate, StringComparison.Ordinal);
    Assert.True(!restored.GetRequired("codex-cli").CommandTemplate.Contains("--sandbox workspace-write", StringComparison.Ordinal));
}
    [Xunit.Fact(DisplayName = "WorkerProfileStore_load_preserves_valid_codex_oss_default_profile")]
    public void WorkerProfileStoreLoadPreservesValidCodexOssDefaultProfile()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "workers.json");
    var saved = WorkerProfileCatalog.Default();
    var expected = saved.GetRequired("codex-oss-cli").CommandTemplate;

    WorkerProfileStore.Save(path, saved);
    var restored = WorkerProfileStore.Load(path);

    Assert.Equal(expected, restored.GetRequired("codex-oss-cli").CommandTemplate);
}
    [Xunit.Fact(DisplayName = "WorkerProfileStore_load_repairs_claude_profile_with_hardcoded_permission_mode")]
    public void WorkerProfileStoreLoadRepairsClaudeProfileWithHardcodedPermissionMode()
{
    var root = CreateTempDirectory();
    var path = Path.Combine(root, "workers.json");
    var saved = WorkerProfileCatalog.Default()
        .Upsert(new WorkerProfile("claude-cli", "claude --model {subscriptionModelName} --permission-mode bypassPermissions -p (Get-Content -Raw {promptPath})"));

    WorkerProfileStore.Save(path, saved);
    var restored = WorkerProfileStore.Load(path);

    Assert.Contains("--permission-mode {permissionMode}", restored.GetRequired("claude-cli").CommandTemplate, StringComparison.Ordinal);
    Assert.True(!restored.GetRequired("claude-cli").CommandTemplate.Contains("--permission-mode bypassPermissions", StringComparison.Ordinal));
}
}

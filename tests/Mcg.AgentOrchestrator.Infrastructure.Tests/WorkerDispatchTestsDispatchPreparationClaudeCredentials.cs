using Mcg.AgentOrchestrator.Infrastructure;
using System;
using System.Diagnostics;
using System.IO;

/// <summary>
/// Claude credential source selection + preflight + sandbox seeding regressions.
/// Every fixture is synthetic and lives under this class's own temp root; no test here reads the
/// operator's real Claude/Codex/Grok auth files, mutates process-global environment state, or makes
/// a network call. The token strings below are knowingly invalid: they exercise LOCAL MATERIAL
/// presence only and are never proof of authentication. Real subscription round-trip verification
/// remains operator-owned.
/// </summary>
public sealed class WorkerDispatchTestsDispatchPreparationClaudeCredentials
{
    private const string ValidSyntheticCredentials =
        "{\"claudeAiOauth\":{\"accessToken\":\"sk-ant-oat01-synthetic-not-a-real-token\",\"refreshToken\":\"sk-ant-ort01-synthetic\",\"expiresAt\":4102444800000}}";

    private const string WrongProfileSentinelCredentials =
        "{\"claudeAiOauth\":{\"accessToken\":\"sk-ant-oat01-WRONG-PROFILE-SENTINEL\"}}";

    private const string DestinationExistingLoginCredentials =
        "{\"claudeAiOauth\":{\"accessToken\":\"sk-ant-oat01-DESTINATION-EXISTING-LOGIN\"}}";

    private const string EmptyTokenCredentials =
        "{\"claudeAiOauth\":{\"accessToken\":\"\",\"refreshToken\":\"\"}}";

    private const string MalformedCredentials = "{\"claudeAiOauth\": {\"accessToken\": ";

    [Xunit.Fact(DisplayName = "Claude_explicit_config_dir_drives_preflight_selection_and_seeding")]
    public void ExplicitConfigDirectoryDrivesPreflightSelectionAndSeeding()
    {
        var root = CreateFixtureRoot();
        try
        {
            var explicitDir = WriteProfile(root, "operator-config", ValidSyntheticCredentials, "{\"theme\":\"dark\"}");
            var defaultHome = Path.Combine(root, "default-home");
            WriteProfile(defaultHome, ".claude", WrongProfileSentinelCredentials);

            var environmentReader = ClaudeEnvironment(configDirectory: explicitDir);
            Func<string?> defaultHomeProvider = () => defaultHome;

            // Preflight and seeding must agree on the same source, selected from the same inputs.
            var auth = ClaudeCliAuthProbe.From(environmentReader, defaultHomeProvider);
            Assert.False(auth.HasAnthropicApiKey);
            Assert.True(auth.HasCliCredentialArtifact);
            Assert.Equal(
                Path.Combine(Path.GetFullPath(explicitDir), ".credentials.json"),
                auth.CredentialArtifactPath);

            var startInfo = CreateStartInfo(root);
            var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
            var stderrPath = Path.Combine(root, "dispatch.stderr.log");

            DispatchProcessHost.SeedProviderEnvironment(
                startInfo,
                WorkerSandboxProvider.Claude,
                sandboxRoot,
                stderrPath,
                anthropicApiKeyAccessor: null,
                claudeCredentialDirectoryAccessor: null,
                environmentReader: environmentReader,
                defaultHomeProvider: defaultHomeProvider);

            Assert.True(startInfo.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var seededDir));
            var seeded = File.ReadAllText(Path.Combine(seededDir!, ".credentials.json"));
            Assert.Equal(ValidSyntheticCredentials, seeded);
            Assert.DoesNotContain("WRONG-PROFILE-SENTINEL", seeded);
            Assert.Equal("{\"theme\":\"dark\"}", File.ReadAllText(Path.Combine(seededDir!, "settings.json")));
            Assert.Equal(string.Empty, ReadDiagnostics(stderrPath));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Xunit.Fact(DisplayName = "Claude_missing_explicit_config_dir_stops_dispatch_and_never_uses_default_profile")]
    public void MissingExplicitConfigDirectoryStopsDispatchAndNeverUsesDefaultProfile()
    {
        var root = CreateFixtureRoot();
        try
        {
            var defaultHome = Path.Combine(root, "default-home");
            WriteProfile(defaultHome, ".claude", WrongProfileSentinelCredentials);
            var missingExplicitDir = Path.Combine(root, "operator-config-missing");

            var environmentReader = ClaudeEnvironment(configDirectory: missingExplicitDir);
            Func<string?> defaultHomeProvider = () => defaultHome;

            var auth = ClaudeCliAuthProbe.From(environmentReader, defaultHomeProvider);
            Assert.False(auth.HasCliCredentialArtifact);
            Assert.Null(auth.CredentialArtifactPath);

            var startInfo = CreateStartInfo(root);
            var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
            var stderrPath = Path.Combine(root, "dispatch.stderr.log");

            var failure = Assert.Throws<WorkerSubscriptionPreflightException>(() =>
                DispatchProcessHost.SeedProviderEnvironment(
                    startInfo,
                    WorkerSandboxProvider.Claude,
                    sandboxRoot,
                    stderrPath,
                    anthropicApiKeyAccessor: null,
                    claudeCredentialDirectoryAccessor: null,
                    environmentReader: environmentReader,
                    defaultHomeProvider: defaultHomeProvider));

            Assert.Contains(ClaudeCliAuthProbe.AuthUnavailableErrorCode, failure.Message);
            Assert.Contains("CLAUDE_CONFIG_DIR", failure.Message);
            Assert.DoesNotContain("sk-ant-", failure.Message);
            Assert.DoesNotContain("claudeAiOauth", failure.Message);

            // Nothing may be launched, and no destination artifact may be created.
            Assert.False(startInfo.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
            Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "claude-config")));

            var diagnostics = ReadDiagnostics(stderrPath);
            Assert.Contains(ClaudeCliAuthProbe.AuthUnavailableErrorCode, diagnostics);
            Assert.Contains("Claude", diagnostics);
            Assert.DoesNotContain("WRONG-PROFILE-SENTINEL", diagnostics);
            Assert.DoesNotContain("sk-ant-", diagnostics);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Xunit.Fact(DisplayName = "Claude_default_profile_is_seeded_when_no_explicit_config_dir_is_set")]
    public void DefaultProfileIsSeededWhenNoExplicitConfigDirectoryIsSet()
    {
        var root = CreateFixtureRoot();
        try
        {
            var defaultHome = Path.Combine(root, "default-home");
            WriteProfile(defaultHome, ".claude", ValidSyntheticCredentials, "{\"theme\":\"light\"}");

            var environmentReader = ClaudeEnvironment();
            Func<string?> defaultHomeProvider = () => defaultHome;

            var auth = ClaudeCliAuthProbe.From(environmentReader, defaultHomeProvider);
            Assert.True(auth.HasCliCredentialArtifact);
            Assert.Equal(
                Path.Combine(Path.GetFullPath(Path.Combine(defaultHome, ".claude")), ".credentials.json"),
                auth.CredentialArtifactPath);

            var startInfo = CreateStartInfo(root);
            var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
            var stderrPath = Path.Combine(root, "dispatch.stderr.log");

            DispatchProcessHost.SeedProviderEnvironment(
                startInfo,
                WorkerSandboxProvider.Claude,
                sandboxRoot,
                stderrPath,
                anthropicApiKeyAccessor: null,
                claudeCredentialDirectoryAccessor: null,
                environmentReader: environmentReader,
                defaultHomeProvider: defaultHomeProvider);

            Assert.True(startInfo.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var seededDir));
            Assert.Equal(
                ValidSyntheticCredentials,
                File.ReadAllText(Path.Combine(seededDir!, ".credentials.json")));
            Assert.Equal("{\"theme\":\"light\"}", File.ReadAllText(Path.Combine(seededDir!, "settings.json")));
            Assert.Equal(string.Empty, ReadDiagnostics(stderrPath));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Xunit.Fact(DisplayName = "Claude_preferences_and_claude_json_alone_are_not_local_auth_material")]
    public void PreferencesAndClaudeJsonAloneAreNotLocalAuthMaterial()
    {
        var root = CreateFixtureRoot();
        try
        {
            var defaultHome = Path.Combine(root, "default-home");
            var profileDir = Path.Combine(defaultHome, ".claude");
            Directory.CreateDirectory(profileDir);
            File.WriteAllText(Path.Combine(profileDir, "settings.json"), "{\"theme\":\"dark\"}");
            File.WriteAllText(Path.Combine(profileDir, "config.json"), "{\"autoUpdates\":true}");
            File.WriteAllText(Path.Combine(profileDir, "preferences.json"), "{\"editorMode\":\"vim\"}");
            File.WriteAllText(Path.Combine(defaultHome, ".claude.json"), ValidSyntheticCredentials);

            var environmentReader = ClaudeEnvironment();
            Func<string?> defaultHomeProvider = () => defaultHome;

            var auth = ClaudeCliAuthProbe.From(environmentReader, defaultHomeProvider);
            Assert.False(auth.HasCliCredentialArtifact);
            Assert.Null(auth.CredentialArtifactPath);

            var startInfo = CreateStartInfo(root);
            var sandboxRoot = Path.Combine(root, ".mcg-sandbox");

            var failure = Assert.Throws<WorkerSubscriptionPreflightException>(() =>
                DispatchProcessHost.SeedProviderEnvironment(
                    startInfo,
                    WorkerSandboxProvider.Claude,
                    sandboxRoot,
                    stderrPath: null,
                    anthropicApiKeyAccessor: null,
                    claudeCredentialDirectoryAccessor: null,
                    environmentReader: environmentReader,
                    defaultHomeProvider: defaultHomeProvider));

            Assert.Contains(ClaudeCliAuthProbe.AuthUnavailableErrorCode, failure.Message);
            Assert.Contains(".credentials.json", failure.Message);
            Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "claude-config")));
            Assert.False(startInfo.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Xunit.Fact(DisplayName = "Claude_empty_oauth_token_source_stops_dispatch_and_preserves_destination_login")]
    public void EmptyOAuthTokenSourceStopsDispatchAndPreservesDestinationLogin()
    {
        AssertUnusableSourcePreservesDestination(EmptyTokenCredentials, "empty-token-source");
    }

    [Xunit.Fact(DisplayName = "Claude_malformed_credential_source_stops_dispatch_and_preserves_destination_login")]
    public void MalformedCredentialSourceStopsDispatchAndPreservesDestinationLogin()
    {
        AssertUnusableSourcePreservesDestination(MalformedCredentials, "malformed-source");
    }

    [Xunit.Fact(DisplayName = "Claude_valid_same_location_source_is_not_copied_and_keeps_its_bytes")]
    public void ValidSameLocationSourceIsNotCopiedAndKeepsItsBytes()
    {
        var root = CreateFixtureRoot();
        try
        {
            var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
            var destination = Path.Combine(sandboxRoot, "claude-config");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, ".credentials.json"), ValidSyntheticCredentials);
            File.WriteAllText(Path.Combine(destination, "settings.json"), "{\"theme\":\"dark\"}");

            var startInfo = CreateStartInfo(root);
            var stderrPath = Path.Combine(root, "dispatch.stderr.log");

            DispatchProcessHost.SeedProviderEnvironment(
                startInfo,
                WorkerSandboxProvider.Claude,
                sandboxRoot,
                stderrPath,
                anthropicApiKeyAccessor: () => null,
                claudeCredentialDirectoryAccessor: () => AliasSameLocation(destination));

            Assert.True(startInfo.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var seededDir));
            Assert.Equal(
                ValidSyntheticCredentials,
                File.ReadAllText(Path.Combine(seededDir!, ".credentials.json")));
            Assert.Equal("{\"theme\":\"dark\"}", File.ReadAllText(Path.Combine(seededDir!, "settings.json")));
            Assert.Equal(string.Empty, ReadDiagnostics(stderrPath));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Xunit.Fact(DisplayName = "Claude_invalid_same_location_source_stops_dispatch_and_keeps_its_bytes")]
    public void InvalidSameLocationSourceStopsDispatchAndKeepsItsBytes()
    {
        var root = CreateFixtureRoot();
        try
        {
            var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
            var destination = Path.Combine(sandboxRoot, "claude-config");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, ".credentials.json"), EmptyTokenCredentials);

            var startInfo = CreateStartInfo(root);
            var stderrPath = Path.Combine(root, "dispatch.stderr.log");

            // The same-location shortcut must not run before validation: an unusable source stays a
            // typed failure even when it already is the destination, and its bytes are retained.
            var failure = Assert.Throws<WorkerSubscriptionPreflightException>(() =>
                DispatchProcessHost.SeedProviderEnvironment(
                    startInfo,
                    WorkerSandboxProvider.Claude,
                    sandboxRoot,
                    stderrPath,
                    anthropicApiKeyAccessor: () => null,
                    claudeCredentialDirectoryAccessor: () => AliasSameLocation(destination)));

            Assert.Contains(ClaudeCliAuthProbe.AuthUnavailableErrorCode, failure.Message);
            Assert.False(startInfo.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
            Assert.Equal(
                EmptyTokenCredentials,
                File.ReadAllText(Path.Combine(destination, ".credentials.json")));
            Assert.False(File.Exists(Path.Combine(destination, "settings.json")));
            Assert.DoesNotContain("sk-ant-", ReadDiagnostics(stderrPath));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Xunit.Fact(DisplayName = "Claude_api_key_mode_bypasses_subscription_source_validation")]
    public void ApiKeyModeBypassesSubscriptionSourceValidation()
    {
        var root = CreateFixtureRoot();
        try
        {
            var startInfo = CreateStartInfo(root);
            var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
            var stderrPath = Path.Combine(root, "dispatch.stderr.log");
            var missingConfigDir = Path.Combine(root, "operator-config-missing");

            DispatchProcessHost.SeedProviderEnvironment(
                startInfo,
                WorkerSandboxProvider.Claude,
                sandboxRoot,
                stderrPath,
                anthropicApiKeyAccessor: () => "sk-ant-api03-synthetic-test-key",
                claudeCredentialDirectoryAccessor: null,
                environmentReader: ClaudeEnvironment(configDirectory: missingConfigDir),
                defaultHomeProvider: () => Path.Combine(root, "default-home"));

            Assert.True(startInfo.Environment.TryGetValue("ANTHROPIC_API_KEY", out var apiKey));
            Assert.Equal("sk-ant-api03-synthetic-test-key", apiKey);
            Assert.True(startInfo.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var seededDir));
            Assert.False(File.Exists(Path.Combine(seededDir!, ".credentials.json")));
            Assert.True(File.Exists(Path.Combine(seededDir!, "settings.json")));
            Assert.Equal(string.Empty, ReadDiagnostics(stderrPath));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Xunit.Fact(DisplayName = "Claude_seeding_leaves_non_claude_provider_artifacts_untouched")]
    public void ClaudeSeedingLeavesNonClaudeProviderArtifactsUntouched()
    {
        var root = CreateFixtureRoot();
        try
        {
            var explicitDir = WriteProfile(root, "operator-config", ValidSyntheticCredentials);
            var startInfo = CreateStartInfo(root);

            // Stale non-Claude values on the child start-info copy (never the test process env).
            startInfo.Environment["CODEX_HOME"] = Path.Combine(root, "stale-codex");
            startInfo.Environment["GROK_HOME"] = Path.Combine(root, "stale-grok");
            startInfo.Environment["HERMES_HOME"] = Path.Combine(root, "stale-hermes");

            var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
            var stderrPath = Path.Combine(root, "dispatch.stderr.log");

            DispatchProcessHost.SeedProviderEnvironment(
                startInfo,
                WorkerSandboxProvider.Claude,
                sandboxRoot,
                stderrPath,
                anthropicApiKeyAccessor: () => null,
                claudeCredentialDirectoryAccessor: () => explicitDir);

            Assert.False(startInfo.Environment.ContainsKey("CODEX_HOME"));
            Assert.False(startInfo.Environment.ContainsKey("GROK_HOME"));
            Assert.False(startInfo.Environment.ContainsKey("HERMES_HOME"));
            Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "codex-home")));
            Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "grok-home")));
            Assert.True(startInfo.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var seededDir));
            Assert.Equal(Path.Combine(sandboxRoot, "claude-config"), seededDir);
            Assert.Equal(
                ValidSyntheticCredentials,
                File.ReadAllText(Path.Combine(seededDir!, ".credentials.json")));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static void AssertUnusableSourcePreservesDestination(string sourceCredentials, string profileName)
    {
        var root = CreateFixtureRoot();
        try
        {
            var sourceDir = WriteProfile(root, profileName, sourceCredentials, "{\"theme\":\"source\"}");

            var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
            var destination = Path.Combine(sandboxRoot, "claude-config");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, ".credentials.json"), DestinationExistingLoginCredentials);
            File.WriteAllText(Path.Combine(destination, "settings.json"), "{\"theme\":\"destination\"}");

            var startInfo = CreateStartInfo(root);
            var stderrPath = Path.Combine(root, "dispatch.stderr.log");

            var failure = Assert.Throws<WorkerSubscriptionPreflightException>(() =>
                DispatchProcessHost.SeedProviderEnvironment(
                    startInfo,
                    WorkerSandboxProvider.Claude,
                    sandboxRoot,
                    stderrPath,
                    anthropicApiKeyAccessor: () => null,
                    claudeCredentialDirectoryAccessor: () => sourceDir));

            Assert.Contains(ClaudeCliAuthProbe.AuthUnavailableErrorCode, failure.Message);
            Assert.False(startInfo.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));

            // Destination login must survive byte-for-byte: no overwrite, no delete, no launch.
            Assert.Equal(
                DestinationExistingLoginCredentials,
                File.ReadAllText(Path.Combine(destination, ".credentials.json")));
            Assert.Equal(
                "{\"theme\":\"destination\"}",
                File.ReadAllText(Path.Combine(destination, "settings.json")));

            var diagnostics = ReadDiagnostics(stderrPath);
            Assert.Contains(ClaudeCliAuthProbe.AuthUnavailableErrorCode, diagnostics);
            Assert.DoesNotContain("sk-ant-", diagnostics);
            Assert.DoesNotContain("claudeAiOauth", diagnostics);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    private static Func<string, string?> ClaudeEnvironment(
        string? configDirectory = null,
        string? anthropicApiKey = null) =>
        name => name switch
        {
            "CLAUDE_CONFIG_DIR" => configDirectory,
            "ANTHROPIC_API_KEY" => anthropicApiKey,
            _ => null,
        };

    private static string AliasSameLocation(string directory)
    {
        var aliased = directory + Path.DirectorySeparatorChar;
        return OperatingSystem.IsWindows() ? aliased.ToUpperInvariant() : aliased;
    }

    private static string CreateFixtureRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-claude-credential-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteProfile(
        string parentDirectory,
        string profileName,
        string credentialsJson,
        string? settingsJson = null)
    {
        var directory = Path.Combine(parentDirectory, profileName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ".credentials.json"), credentialsJson);
        if (settingsJson is not null)
        {
            File.WriteAllText(Path.Combine(directory, "settings.json"), settingsJson);
        }

        return directory;
    }

    private static ProcessStartInfo CreateStartInfo(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
        };

        // Ambient operator credentials must not influence these assertions. This mutates only the
        // child start-info copy, never the test process environment.
        startInfo.Environment.Remove("ANTHROPIC_API_KEY");
        startInfo.Environment.Remove("CLAUDE_CONFIG_DIR");
        return startInfo;
    }

    private static string ReadDiagnostics(string stderrPath) =>
        File.Exists(stderrPath) ? File.ReadAllText(stderrPath) : string.Empty;

    private static void DeleteQuietly(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}

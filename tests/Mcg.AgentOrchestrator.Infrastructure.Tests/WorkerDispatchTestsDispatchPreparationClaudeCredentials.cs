using Mcg.AgentOrchestrator.Infrastructure;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

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

    // Reason phrases owned by ClaudeCredentialSource.Describe. They are asserted here as distinct
    // strings so a future edit cannot collapse two different failure shapes into one message.
    private const string MissingFileReason = ".credentials.json is absent";
    private const string EmptyFileReason = ".credentials.json is empty";
    private const string UnreadableFileReason = ".credentials.json could not be read";
    private const string MalformedFileReason = ".credentials.json is not a readable JSON object";
    private const string NotTransportedReason = "dispatch carried no resolved credential source selection";

    [Xunit.Fact(DisplayName = "Claude_explicit_config_dir_drives_preflight_selection_and_seeding")]
    public void ExplicitConfigDirectoryDrivesPreflightSelectionAndSeeding()
    {
        var root = CreateFixtureRoot();
        try
        {
            var explicitDir = WriteProfile(root, "operator-config", ValidSyntheticCredentials, "{\"theme\":\"dark\"}");
            var defaultHome = Path.Combine(root, "default-home");
            WriteProfile(defaultHome, ".claude", WrongProfileSentinelCredentials);

            // ONE resolver, shared by preflight and seeding: the resolved result preflight reports is
            // the object handed to seeding, and the counting inputs prove nothing resolved twice.
            var inputs = new CountingSelectionInputs(configDirectory: explicitDir, defaultHome: defaultHome);
            var resolver = new ClaudeCredentialResolver(inputs.EnvironmentReader, inputs.DefaultHomeProvider);

            var auth = ClaudeCliAuthProbe.From(resolver);
            Assert.False(auth.HasAnthropicApiKey);
            Assert.True(auth.HasCliCredentialArtifact);
            Assert.Equal(
                Path.Combine(Path.GetFullPath(explicitDir), ".credentials.json"),
                auth.CredentialArtifactPath);

            var startInfo = CreateStartInfo(root);
            var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
            var stderrPath = Path.Combine(root, "dispatch.stderr.log");

            var seededSource = DispatchProcessHost.SeedProviderEnvironment(
                startInfo,
                WorkerSandboxProvider.Claude,
                sandboxRoot,
                stderrPath,
                anthropicApiKeyAccessor: null,
                claudeCredentialDirectoryAccessor: null,
                claudeCredentialResolver: resolver);

            Assert.True(startInfo.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var seededDir));
            var seeded = File.ReadAllText(Path.Combine(seededDir!, ".credentials.json"));
            Assert.Equal(ValidSyntheticCredentials, seeded);
            Assert.DoesNotContain("WRONG-PROFILE-SENTINEL", seeded);
            Assert.Equal("{\"theme\":\"dark\"}", File.ReadAllText(Path.Combine(seededDir!, "settings.json")));
            Assert.Equal(string.Empty, ReadDiagnostics(stderrPath));

            AssertPreflightAndSeedingAgreeOnOneResolvedSource(
                auth,
                seededSource,
                resolver,
                inputs,
                explicitDir,
                seededDir!);
            Assert.True(auth.IsExplicitSource);

            // The rejected candidate was never even looked at: explicit selection does not consult the
            // default profile, so its home root was never read.
            Assert.Equal(0, inputs.DefaultHomeReads);

            // Both consumers answered the API-key question through the shared injected environment -
            // one read each - so neither can reach process state for auth mode while the other does not.
            Assert.Equal(2, inputs.ApiKeyReads);
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

            var inputs = new CountingSelectionInputs(configDirectory: null, defaultHome: defaultHome);
            var resolver = new ClaudeCredentialResolver(inputs.EnvironmentReader, inputs.DefaultHomeProvider);

            var auth = ClaudeCliAuthProbe.From(resolver);
            Assert.True(auth.HasCliCredentialArtifact);
            Assert.Equal(
                Path.Combine(Path.GetFullPath(Path.Combine(defaultHome, ".claude")), ".credentials.json"),
                auth.CredentialArtifactPath);

            var startInfo = CreateStartInfo(root);
            var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
            var stderrPath = Path.Combine(root, "dispatch.stderr.log");

            var seededSource = DispatchProcessHost.SeedProviderEnvironment(
                startInfo,
                WorkerSandboxProvider.Claude,
                sandboxRoot,
                stderrPath,
                anthropicApiKeyAccessor: null,
                claudeCredentialDirectoryAccessor: null,
                claudeCredentialResolver: resolver);

            Assert.True(startInfo.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var seededDir));
            Assert.Equal(
                ValidSyntheticCredentials,
                File.ReadAllText(Path.Combine(seededDir!, ".credentials.json")));
            Assert.Equal("{\"theme\":\"light\"}", File.ReadAllText(Path.Combine(seededDir!, "settings.json")));
            Assert.Equal(string.Empty, ReadDiagnostics(stderrPath));

            AssertPreflightAndSeedingAgreeOnOneResolvedSource(
                auth,
                seededSource,
                resolver,
                inputs,
                Path.Combine(defaultHome, ".claude"),
                seededDir!);
            Assert.False(auth.IsExplicitSource);

            // The fallback candidate was evaluated exactly once for the shared resolution.
            Assert.Equal(1, inputs.DefaultHomeReads);
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

    [Xunit.Fact(DisplayName = "Claude_preflight_reports_the_rejected_explicit_source_and_its_reason")]
    public void ClaudePreflightReportsTheRejectedExplicitSourceAndItsReason()
    {
        var root = CreateFixtureRoot();
        try
        {
            var defaultHome = Path.Combine(root, "default-home");
            WriteProfile(defaultHome, ".claude", WrongProfileSentinelCredentials);
            var missingExplicitDir = Path.Combine(root, "operator-config-missing");

            var auth = ClaudeCliAuthProbe.From(
                ClaudeEnvironment(configDirectory: missingExplicitDir),
                () => defaultHome);

            Assert.False(auth.HasCliCredentialArtifact);
            Assert.Null(auth.CredentialArtifactPath);

            // The attempted candidate must survive a failed probe. Reporting only "no artifact"
            // leaves an operator unable to tell WHICH login source preflight rejected, which is the
            // exact gap that let preflight and seeding disagree unnoticed.
            Assert.True(auth.IsExplicitSource);
            Assert.Equal(Path.GetFullPath(missingExplicitDir), auth.SelectedSourceDirectory);
            Assert.NotNull(auth.UnavailableReason);
            Assert.Contains("CLAUDE_CONFIG_DIR", auth.UnavailableReason!);
            Assert.Contains(Path.GetFullPath(missingExplicitDir), auth.UnavailableReason!);
            Assert.Contains("the directory does not exist", auth.UnavailableReason!);

            // Explicit source never falls through, so the default profile must appear nowhere in the
            // reported candidate set.
            Assert.DoesNotContain("WRONG-PROFILE-SENTINEL", auth.UnavailableReason!);
            Assert.DoesNotContain(
                Path.GetFullPath(Path.Combine(defaultHome, ".claude")),
                auth.UnavailableReason!);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Xunit.Fact(DisplayName = "Claude_preflight_reports_the_rejected_default_profile_source_and_its_reason")]
    public void ClaudePreflightReportsTheRejectedDefaultProfileSourceAndItsReason()
    {
        var root = CreateFixtureRoot();
        try
        {
            var defaultHome = Path.Combine(root, "default-home");
            Directory.CreateDirectory(Path.Combine(defaultHome, ".claude"));

            var auth = ClaudeCliAuthProbe.From(ClaudeEnvironment(), () => defaultHome);

            Assert.False(auth.HasCliCredentialArtifact);
            Assert.False(auth.IsExplicitSource);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(defaultHome, ".claude")),
                auth.SelectedSourceDirectory);
            Assert.NotNull(auth.UnavailableReason);
            Assert.Contains("default profile", auth.UnavailableReason!);
            Assert.Contains(Path.GetFullPath(Path.Combine(defaultHome, ".claude")), auth.UnavailableReason!);
            Assert.Contains(MissingFileReason, auth.UnavailableReason!);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Xunit.Fact(DisplayName = "Claude_unusable_source_reasons_stay_distinct_per_failure_shape")]
    public void ClaudeUnusableSourceReasonsStayDistinctPerFailureShape()
    {
        var missing = CaptureSeedFailure(directory => Directory.CreateDirectory(directory));
        var empty = CaptureSeedFailure(directory => WriteCredentialFile(directory, string.Empty));
        var malformed = CaptureSeedFailure(directory => WriteCredentialFile(directory, MalformedCredentials));

        // Each failure shape keeps its own phrase: 'missing' must never read as 'unparseable'.
        Assert.Contains(MissingFileReason, missing.Message);
        Assert.DoesNotContain(EmptyFileReason, missing.Message);
        Assert.DoesNotContain(MalformedFileReason, missing.Message);
        Assert.DoesNotContain(UnreadableFileReason, missing.Message);

        Assert.Contains(EmptyFileReason, empty.Message);
        Assert.DoesNotContain(MissingFileReason, empty.Message);
        Assert.DoesNotContain(MalformedFileReason, empty.Message);

        Assert.Contains(MalformedFileReason, malformed.Message);
        Assert.DoesNotContain(MissingFileReason, malformed.Message);
        Assert.DoesNotContain(EmptyFileReason, malformed.Message);

        // All three remain the same typed pre-launch failure, and each says so on stderr too.
        foreach (var failure in new[] { missing, empty, malformed })
        {
            Assert.Equal(ClaudeCliAuthProbe.AuthUnavailableErrorCode, failure.ErrorCode);
            Assert.Contains(ClaudeCliAuthProbe.AuthUnavailableErrorCode, failure.Diagnostics);
        }
    }

    [Xunit.Fact(DisplayName = "Claude_unreadable_credential_file_reports_its_own_reason")]
    public void ClaudeUnreadableCredentialFileReportsItsOwnReason()
    {
        // Share-mode locking is only enforced on Windows; elsewhere the file would read fine and the
        // assertion below would be testing nothing.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The exclusive handle is owned by the capture helper, which releases it before fixture
        // cleanup so a locked file cannot leak temp state.
        var failure = CaptureSeedFailureHoldingSource(directory =>
        {
            WriteCredentialFile(directory, ValidSyntheticCredentials);
            return new FileStream(
                Path.Combine(directory, ".credentials.json"),
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
        });

        // Present-but-unreadable is its own reason: an operator must not be told the file is absent.
        Assert.Equal(ClaudeCliAuthProbe.AuthUnavailableErrorCode, failure.ErrorCode);
        Assert.Contains(UnreadableFileReason, failure.Message);
        Assert.DoesNotContain(MissingFileReason, failure.Message);
        Assert.DoesNotContain(MalformedFileReason, failure.Message);
        Assert.DoesNotContain(EmptyFileReason, failure.Message);
    }

    [Xunit.Fact(DisplayName = "Claude_credential_diagnostics_never_reproduce_source_token_material")]
    public void ClaudeCredentialDiagnosticsNeverReproduceSourceTokenMaterial()
    {
        // Empty access token with the secret parked in the refresh-token position.
        AssertSentinelIsNeverEmitted(sentinel =>
            "{\"claudeAiOauth\":{\"accessToken\":\"\",\"refreshToken\":\"" + sentinel + "\"}}");

        // Unparseable text: the secret sits in the raw bytes the parser choked on.
        AssertSentinelIsNeverEmitted(sentinel =>
            "{\"claudeAiOauth\":{\"accessToken\":\"" + sentinel + "\"");
    }

    [Xunit.Fact(DisplayName = "Claude_seeding_writes_only_inside_the_sandbox_and_leaves_the_host_source_unmutated")]
    public void ClaudeSeedingWritesOnlyInsideTheSandboxAndLeavesTheHostSourceUnmutated()
    {
        var root = CreateFixtureRoot();
        try
        {
            var defaultHome = Path.Combine(root, "default-home");
            var sourceDir = WriteProfile(defaultHome, ".claude", ValidSyntheticCredentials, "{\"theme\":\"host\"}");
            File.WriteAllText(Path.Combine(sourceDir, "config.json"), "{\"autoUpdates\":true}");
            var sourceCredentialFile = Path.GetFullPath(Path.Combine(sourceDir, ".credentials.json"));
            var hostSnapshot = SnapshotDirectory(sourceDir);

            var environmentReader = ClaudeEnvironment();
            Func<string?> defaultHomeProvider = () => defaultHome;
            var startInfo = CreateStartInfo(root);
            var sandboxRoot = Path.Combine(root, ".mcg-sandbox");

            DispatchProcessHost.SeedProviderEnvironment(
                startInfo,
                WorkerSandboxProvider.Claude,
                sandboxRoot,
                stderrPath: null,
                anthropicApiKeyAccessor: null,
                claudeCredentialDirectoryAccessor: null,
                environmentReader: environmentReader,
                defaultHomeProvider: defaultHomeProvider);

            Assert.True(startInfo.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var seededDir));

            // Seeding is a one-way copy: the host login is read, never rewritten, and nothing is
            // added beside it. Compared by content hash so no credential bytes reach test output.
            Assert.Equal(hostSnapshot, SnapshotDirectory(sourceDir));

            // Credential material exists in exactly two places: the host source and this sandbox.
            var sandboxPrefix = Path.GetFullPath(sandboxRoot) + Path.DirectorySeparatorChar;
            var credentialFiles = Directory
                .EnumerateFiles(root, ".credentials.json", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(2, credentialFiles.Length);
            Assert.Contains(sourceCredentialFile, credentialFiles);
            Assert.All(
                credentialFiles.Where(path => !PathsMatch(path, sourceCredentialFile)),
                path => Assert.StartsWith(sandboxPrefix, path, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(
                Path.GetFullPath(Path.Combine(sandboxRoot, "claude-config")),
                Path.GetFullPath(seededDir!));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    /// <summary>
    /// Proves the selection was TRANSPORTED, not agreed on by coincidence: the resolved result seeding
    /// consumed is the same object preflight reported, and selection ran exactly once. Comparing paths
    /// or file hashes alone cannot fail when two independent resolutions read the same fixture, which
    /// is why reference identity and the input-read counters carry the contract here.
    /// </summary>
    private static void AssertPreflightAndSeedingAgreeOnOneResolvedSource(
        ClaudeCliAuthState auth,
        ClaudeCredentialResolution? seedingConsumed,
        ClaudeCredentialResolver resolver,
        CountingSelectionInputs inputs,
        string expectedSourceDirectory,
        string seededDirectory)
    {
        // Identity, not equality: seeding consumed preflight's resolved result itself.
        Assert.NotNull(auth.Resolution);
        Assert.NotNull(seedingConsumed);
        Assert.Same(auth.Resolution, seedingConsumed);

        // And it was computed once. Any second resolution - a consumer building its own resolver, or
        // reaching past it to re-evaluate candidates - must read a selection input again, so these
        // counters fail instead of silently agreeing.
        Assert.Equal(1, resolver.ResolutionCount);
        Assert.Equal(1, inputs.ConfigDirectoryReads);

        Assert.NotNull(auth.CredentialArtifactPath);
        Assert.Null(auth.UnavailableReason);
        Assert.Equal(Path.GetFullPath(expectedSourceDirectory), auth.SelectedSourceDirectory);
        Assert.Equal(auth.SelectedSourceDirectory, seedingConsumed!.Inspection.DirectoryPath);
        Assert.Equal(auth.SelectedSourceDirectory, Path.GetDirectoryName(auth.CredentialArtifactPath));
        Assert.Equal(
            HashFile(auth.CredentialArtifactPath!),
            HashFile(Path.Combine(seededDirectory, ".credentials.json")));
    }

    [Xunit.Fact(DisplayName = "Claude_preflight_and_seeding_share_one_resolution_when_the_source_is_rejected")]
    public void ClaudePreflightAndSeedingShareOneResolutionWhenTheSourceIsRejected()
    {
        var root = CreateFixtureRoot();
        try
        {
            // Rejected explicit source, with a usable default profile beside it that must stay unused.
            var missingExplicitDir = Path.Combine(root, "operator-config-missing");
            var defaultHome = Path.Combine(root, "default-home");
            WriteProfile(defaultHome, ".claude", WrongProfileSentinelCredentials);

            var inputs = new CountingSelectionInputs(
                configDirectory: missingExplicitDir,
                defaultHome: defaultHome);
            var resolver = new ClaudeCredentialResolver(inputs.EnvironmentReader, inputs.DefaultHomeProvider);

            var auth = ClaudeCliAuthProbe.From(resolver);
            Assert.False(auth.HasCliCredentialArtifact);
            Assert.NotNull(auth.Resolution);

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
                    claudeCredentialResolver: resolver));

            // The rejection path transports the same resolution too: the source named in the pre-launch
            // failure is the one preflight reported, resolved once, with no worker started.
            Assert.Equal(1, resolver.ResolutionCount);
            Assert.Equal(1, inputs.ConfigDirectoryReads);
            Assert.Equal(0, inputs.DefaultHomeReads);
            Assert.Same(auth.Resolution, resolver.Resolve());
            Assert.Equal(
                Path.GetFullPath(missingExplicitDir),
                auth.Resolution!.Inspection.DirectoryPath);
            Assert.Contains(auth.Resolution!.Inspection.DirectoryPath!, failure.Message);
            Assert.Contains(auth.Resolution!.Inspection.DirectoryPath!, auth.UnavailableReason!);
            Assert.False(startInfo.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
            Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "claude-config")));
            Assert.DoesNotContain("WRONG-PROFILE-SENTINEL", failure.Message);
            Assert.DoesNotContain("sk-ant-", ReadDiagnostics(stderrPath));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Xunit.Fact(DisplayName = "Claude_dispatch_transports_the_explicit_preflight_selection_into_the_dispatch_host")]
    public void ClaudeDispatchTransportsTheExplicitPreflightSelectionIntoTheDispatchHost()
    {
        AssertDispatchSeedsTheTransportedPreflightSelection(useExplicitConfigDirectory: true);
    }

    [Xunit.Fact(DisplayName = "Claude_dispatch_transports_the_default_profile_preflight_selection_into_the_dispatch_host")]
    public void ClaudeDispatchTransportsTheDefaultProfilePreflightSelectionIntoTheDispatchHost()
    {
        AssertDispatchSeedsTheTransportedPreflightSelection(useExplicitConfigDirectory: false);
    }

    /// <summary>
    /// The PRODUCTION handoff, end to end and across the real dispatch boundary: the conductor resolves
    /// once, its selection is serialized into the dispatch parameters file the detached host reads, and
    /// the host seeds from that decision. The host is deliberately given an environment that resolves to
    /// a DIFFERENT usable login; if it evaluated candidates of its own it would seed that one instead.
    /// No worker process is started.
    /// </summary>
    private static void AssertDispatchSeedsTheTransportedPreflightSelection(bool useExplicitConfigDirectory)
    {
        var root = CreateFixtureRoot();
        try
        {
            var conductorHome = Path.Combine(root, "conductor-home");
            var explicitDir = WriteProfile(root, "operator-config", ValidSyntheticCredentials, "{\"theme\":\"dark\"}");
            var defaultProfileDir = WriteProfile(conductorHome, ".claude", ValidSyntheticCredentials, "{\"theme\":\"light\"}");
            var expectedSourceDirectory = useExplicitConfigDirectory ? explicitDir : defaultProfileDir;

            // What the dispatch host's OWN environment would select: a different, perfectly usable
            // login. Seeding it would be the preflight/seeding drift this contract exists to remove.
            var hostVisibleDir = WriteProfile(root, "host-config", WrongProfileSentinelCredentials);

            // Conductor tick: one resolution, and the selection it reports is the one transported.
            var conductorInputs = new CountingSelectionInputs(
                configDirectory: useExplicitConfigDirectory ? explicitDir : null,
                defaultHome: conductorHome);
            var conductorResolver = new ClaudeCredentialResolver(
                conductorInputs.EnvironmentReader,
                conductorInputs.DefaultHomeProvider);

            var preflight = DispatchProcessHost.PreflightClaudeCredentialSource(
                WorkerSandboxProvider.Claude,
                sandboxLowIntegrity: true,
                conductorResolver);

            Assert.NotNull(preflight);
            var selection = preflight!.ToTransportedSelection();
            Assert.NotNull(selection);
            Assert.Equal(1, conductorResolver.ResolutionCount);
            Assert.Equal(1, conductorInputs.ConfigDirectoryReads);
            Assert.Equal(Path.GetFullPath(expectedSourceDirectory), preflight.SelectedSourceDirectory);

            // Derived from the reported view, not computed beside it.
            Assert.Equal(preflight.SelectedSourceDirectory, selection!.DirectoryPath);
            Assert.Equal(useExplicitConfigDirectory, selection.IsExplicitSource);

            // The real process boundary: the selection is written to the dispatch parameters file the
            // detached host reads back. Nothing else carries it.
            var parametersPath = Path.Combine(root, "dispatch.json");
            DispatchProcessHost.WriteParameters(
                parametersPath,
                CreateClaudeDispatchParameters(root, selection));
            var transported = DispatchProcessHost.ReadParameters(parametersPath);
            Assert.Equal(selection, transported.ClaudeCredentialSelection);

            // Dispatch host: builds its ONE resolver the way production does, from the transported
            // selection plus its own (disagreeing) environment.
            var hostInputs = new CountingSelectionInputs(
                configDirectory: hostVisibleDir,
                defaultHome: Path.Combine(root, "host-home"));
            var hostResolver = DispatchProcessHost.CreateClaudeCredentialResolver(
                transported,
                hostInputs.EnvironmentReader);

            var startInfo = CreateStartInfo(root);
            var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
            var stderrPath = Path.Combine(root, "dispatch.stderr.log");

            var seededSource = DispatchProcessHost.SeedProviderEnvironment(
                startInfo,
                WorkerSandboxProvider.Claude,
                sandboxRoot,
                stderrPath,
                anthropicApiKeyAccessor: null,
                claudeCredentialDirectoryAccessor: null,
                claudeCredentialResolver: hostResolver);

            // The host never evaluated a candidate: no selection input was read in this process.
            Assert.Equal(0, hostInputs.ConfigDirectoryReads);
            Assert.Equal(0, hostInputs.DefaultHomeReads);
            Assert.Equal(1, hostResolver.ResolutionCount);

            // And the source it seeded is the one preflight reported, down to the source kind the
            // setup artifact records.
            Assert.NotNull(seededSource);
            Assert.Equal(preflight.SelectedSourceDirectory, seededSource!.Inspection.DirectoryPath);
            Assert.Equal(selection.IsExplicitSource, seededSource.Inspection.IsExplicitSource);

            Assert.True(startInfo.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var seededDir));
            Assert.Equal(Path.Combine(sandboxRoot, "claude-config"), seededDir);
            var seeded = File.ReadAllText(Path.Combine(seededDir!, ".credentials.json"));
            Assert.Equal(ValidSyntheticCredentials, seeded);
            Assert.DoesNotContain("WRONG-PROFILE-SENTINEL", seeded);
            Assert.Equal(
                HashFile(Path.Combine(Path.GetFullPath(expectedSourceDirectory), ".credentials.json")),
                HashFile(Path.Combine(seededDir!, ".credentials.json")));
            Assert.Equal(string.Empty, ReadDiagnostics(stderrPath));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Xunit.Fact(DisplayName = "Claude_dispatch_transports_a_rejected_selection_so_the_host_fails_on_the_same_source")]
    public void ClaudeDispatchTransportsARejectedSelectionSoTheHostFailsOnTheSameSource()
    {
        var root = CreateFixtureRoot();
        try
        {
            var missingExplicitDir = Path.Combine(root, "operator-config-missing");
            var conductorHome = Path.Combine(root, "conductor-home");
            WriteProfile(conductorHome, ".claude", WrongProfileSentinelCredentials);

            // A rejected source must still travel: otherwise the host would have nothing to fail on and
            // would fall back to selecting a source itself - a different account, silently.
            var preflight = DispatchProcessHost.PreflightClaudeCredentialSource(
                WorkerSandboxProvider.Claude,
                sandboxLowIntegrity: true,
                environmentReader: ClaudeEnvironment(configDirectory: missingExplicitDir),
                defaultHomeProvider: () => conductorHome);

            Assert.False(preflight!.HasCliCredentialArtifact);
            var selection = preflight.ToTransportedSelection();
            Assert.NotNull(selection);
            Assert.Equal(Path.GetFullPath(missingExplicitDir), selection!.DirectoryPath);
            Assert.True(selection.IsExplicitSource);

            var hostInputs = new CountingSelectionInputs(
                configDirectory: WriteProfile(root, "host-config", WrongProfileSentinelCredentials),
                defaultHome: conductorHome);
            var hostResolver = DispatchProcessHost.CreateClaudeCredentialResolver(
                CreateClaudeDispatchParameters(root, selection),
                hostInputs.EnvironmentReader);

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
                    claudeCredentialResolver: hostResolver));

            Assert.Equal(ClaudeCliAuthProbe.AuthUnavailableErrorCode, failure.ErrorCode);
            Assert.Contains(Path.GetFullPath(missingExplicitDir), failure.Message);
            Assert.Contains("the directory does not exist", failure.Message);
            Assert.Equal(0, hostInputs.ConfigDirectoryReads);
            Assert.False(startInfo.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
            Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "claude-config")));
            Assert.DoesNotContain("WRONG-PROFILE-SENTINEL", failure.Message);
            Assert.DoesNotContain("sk-ant-", ReadDiagnostics(stderrPath));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Xunit.Fact(DisplayName = "Claude_dispatch_host_refuses_to_select_a_source_when_the_dispatch_transported_none")]
    public void ClaudeDispatchHostRefusesToSelectASourceWhenTheDispatchTransportedNone()
    {
        var root = CreateFixtureRoot();
        try
        {
            // A perfectly usable login sits in the host's own environment. Seeding it would mean the
            // host chose a source no preflight ever reported, so an untransported dispatch fails loudly.
            var hostVisibleDir = WriteProfile(root, "host-config", ValidSyntheticCredentials);
            var parameters = CreateClaudeDispatchParameters(root, selection: null);

            var startInfo = CreateStartInfo(root);
            var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
            var stderrPath = Path.Combine(root, "dispatch.stderr.log");

            var failure = Assert.Throws<WorkerSubscriptionPreflightException>(() =>
                DispatchProcessHost.SeedProviderEnvironment(
                    startInfo,
                    WorkerSandboxProvider.Claude,
                    sandboxRoot,
                    stderrPath,
                    anthropicApiKeyAccessor: () => null,
                    claudeCredentialDirectoryAccessor: null,
                    claudeCredentialResolver: DispatchProcessHost.CreateClaudeCredentialResolver(parameters)));

            Assert.Equal(ClaudeCliAuthProbe.AuthUnavailableErrorCode, failure.ErrorCode);
            Assert.Contains(NotTransportedReason, failure.Message);

            // A broken dispatch contract must not read as a broken login: an operator who sees this
            // has to repair the dispatch path, not their credentials.
            Assert.DoesNotContain(MissingFileReason, failure.Message);
            Assert.DoesNotContain(MalformedFileReason, failure.Message);
            Assert.False(startInfo.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
            Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "claude-config")));
            Assert.DoesNotContain("sk-ant-", failure.Message);
            Assert.DoesNotContain(Path.GetFullPath(hostVisibleDir), failure.Message);

            // The injected-environment seam stays available for host tests that own their fixture, and
            // it is the ONLY way to seed without a transported selection.
            var seamResolver = DispatchProcessHost.CreateClaudeCredentialResolver(
                parameters,
                ClaudeEnvironment(configDirectory: hostVisibleDir));
            Assert.True(seamResolver.Resolve().HasLocalAuthMaterial);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Xunit.Fact(DisplayName = "Claude_credential_preflight_is_skipped_for_non_claude_and_unsandboxed_dispatches")]
    public void ClaudeCredentialPreflightIsSkippedForNonClaudeAndUnsandboxedDispatches()
    {
        // Nothing to select, so nothing may be read or transported: these dispatches seed no Claude
        // login at all, and a stray resolution here would touch the operator's real credential store.
        Assert.Null(DispatchProcessHost.PreflightClaudeCredentialSource(
            WorkerSandboxProvider.Codex,
            sandboxLowIntegrity: true,
            environmentReader: _ => throw new InvalidOperationException("selection input was read")));

        Assert.Null(DispatchProcessHost.PreflightClaudeCredentialSource(
            WorkerSandboxProvider.Claude,
            sandboxLowIntegrity: false,
            environmentReader: _ => throw new InvalidOperationException("selection input was read")));
    }

    [Xunit.Fact(DisplayName = "Claude_one_dispatch_preflight_probe_resolves_once_for_all_of_its_consumers")]
    public void ClaudeOneDispatchPreflightProbeResolvesOnceForAllOfItsConsumers()
    {
        var root = CreateFixtureRoot();
        try
        {
            var explicitDir = WriteProfile(root, "operator-config", ValidSyntheticCredentials);
            var inputs = new CountingSelectionInputs(configDirectory: explicitDir, defaultHome: root);

            // The probe a dispatch preparation hands to every consumer of its auth answer: subscription
            // model-lane selection, the auth finding, and the selection recorded for the dispatch start
            // boundary. Each consumer calling the probe must receive the FIRST resolution, not its own.
            var probe = ClaudeCliAuthProbe.ForOneDispatchPreflight(
                inputs.EnvironmentReader,
                inputs.DefaultHomeProvider);

            var laneSelectionView = probe();
            var authFindingView = probe();
            var recordedView = probe();

            Assert.Same(laneSelectionView, authFindingView);
            Assert.Same(laneSelectionView, recordedView);
            Assert.Same(laneSelectionView.Resolution, recordedView.Resolution);
            Assert.Equal(1, inputs.ConfigDirectoryReads);
            Assert.Equal(0, inputs.DefaultHomeReads);

            // And the selection every consumer carries away is that one resolution's source.
            Assert.Equal(Path.GetFullPath(explicitDir), recordedView.SelectedSourceDirectory);
            Assert.Equal(
                laneSelectionView.ToTransportedSelection(),
                recordedView.ToTransportedSelection());
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Xunit.Fact(DisplayName = "Claude_dispatch_start_transports_the_recorded_preflight_selection_without_reselecting")]
    public void ClaudeDispatchStartTransportsTheRecordedPreflightSelectionWithoutReselecting()
    {
        var root = CreateFixtureRoot();
        try
        {
            // What the dispatch preflight selected, reported, and recorded on the dispatch record while
            // preparing it - possibly in an earlier tick, or an earlier process.
            var recordedDir = WriteProfile(root, "preflight-selected", ValidSyntheticCredentials);

            // A different, perfectly usable login that the start boundary's own environment would pick.
            // Selecting it here is the preflight/seeding drift this handoff exists to remove.
            var startBoundaryVisibleDir = WriteProfile(root, "start-boundary-config", WrongProfileSentinelCredentials);

            var selection = DispatchProcessHost.TransportedClaudeCredentialSelection(
                recordedSourceDirectory: recordedDir,
                recordedSourceIsExplicit: true,
                WorkerSandboxProvider.Claude,
                sandboxLowIntegrity: true,
                environmentReader: ClaudeEnvironment(configDirectory: startBoundaryVisibleDir),
                defaultHomeProvider: () => root);

            // Consumed verbatim: the recorded decision is transported, not re-derived from anything the
            // start boundary can see.
            Assert.Equal(new ClaudeCredentialSourceSelection(recordedDir, true), selection);

            // The whole chain, to the sandbox: recorded selection to dispatch parameters to the detached
            // host's resolver to the seeded config root. No worker process is started.
            var parametersPath = Path.Combine(root, "dispatch.json");
            DispatchProcessHost.WriteParameters(parametersPath, CreateClaudeDispatchParameters(root, selection));
            var hostInputs = new CountingSelectionInputs(
                configDirectory: startBoundaryVisibleDir,
                defaultHome: root);
            var hostResolver = DispatchProcessHost.CreateClaudeCredentialResolver(
                DispatchProcessHost.ReadParameters(parametersPath),
                hostInputs.EnvironmentReader);

            var startInfo = CreateStartInfo(root);
            var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
            var stderrPath = Path.Combine(root, "dispatch.stderr.log");

            var seededSource = DispatchProcessHost.SeedProviderEnvironment(
                startInfo,
                WorkerSandboxProvider.Claude,
                sandboxRoot,
                stderrPath,
                anthropicApiKeyAccessor: null,
                claudeCredentialDirectoryAccessor: null,
                claudeCredentialResolver: hostResolver);

            Assert.NotNull(seededSource);
            Assert.Equal(Path.GetFullPath(recordedDir), seededSource!.Inspection.DirectoryPath);
            Assert.True(seededSource.Inspection.IsExplicitSource);
            Assert.Equal(0, hostInputs.ConfigDirectoryReads);
            Assert.Equal(0, hostInputs.DefaultHomeReads);

            Assert.True(startInfo.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var seededDir));
            var seeded = File.ReadAllText(Path.Combine(seededDir!, ".credentials.json"));
            Assert.Equal(ValidSyntheticCredentials, seeded);
            Assert.DoesNotContain("WRONG-PROFILE-SENTINEL", seeded);
            Assert.Equal(string.Empty, ReadDiagnostics(stderrPath));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Xunit.Fact(DisplayName = "Claude_dispatch_start_resolves_once_when_the_dispatch_recorded_no_preflight_selection")]
    public void ClaudeDispatchStartResolvesOnceWhenTheDispatchRecordedNoPreflightSelection()
    {
        var root = CreateFixtureRoot();
        try
        {
            // A dispatch recorded before this handoff existed, or one prepared while the worker sandbox
            // was disabled: no preflight ever reported a source, so there is no reported source to
            // contradict and this boundary makes the single selection itself.
            var visibleDir = WriteProfile(root, "start-boundary-config", ValidSyntheticCredentials);
            var inputs = new CountingSelectionInputs(configDirectory: visibleDir, defaultHome: root);

            var selection = DispatchProcessHost.TransportedClaudeCredentialSelection(
                recordedSourceDirectory: null,
                recordedSourceIsExplicit: false,
                WorkerSandboxProvider.Claude,
                sandboxLowIntegrity: true,
                environmentReader: inputs.EnvironmentReader,
                defaultHomeProvider: inputs.DefaultHomeProvider);

            Assert.Equal(
                new ClaudeCredentialSourceSelection(Path.GetFullPath(visibleDir), true),
                selection);

            // Once. A fallback that resolved per consumer would be the original defect wearing a
            // different name.
            Assert.Equal(1, inputs.ConfigDirectoryReads);
            Assert.Equal(0, inputs.DefaultHomeReads);

            // A blank recorded directory is the same "nothing was recorded" case, not a selected source.
            Assert.Equal(
                selection,
                DispatchProcessHost.TransportedClaudeCredentialSelection(
                    recordedSourceDirectory: "   ",
                    recordedSourceIsExplicit: true,
                    WorkerSandboxProvider.Claude,
                    sandboxLowIntegrity: true,
                    environmentReader: ClaudeEnvironment(configDirectory: visibleDir),
                    defaultHomeProvider: () => root));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Xunit.Fact(DisplayName = "Claude_dispatch_start_transports_nothing_for_non_claude_and_unsandboxed_dispatches")]
    public void ClaudeDispatchStartTransportsNothingForNonClaudeAndUnsandboxedDispatches()
    {
        // Nothing to seed, so nothing may be read OR carried: a recorded selection from an earlier
        // preparation must not resurrect Claude seeding for a dispatch that seeds no Claude login, and a
        // stray resolution here would touch the operator's real credential store.
        Func<string, string?> refuseSelectionInput =
            _ => throw new InvalidOperationException("selection input was read");

        Assert.Null(DispatchProcessHost.TransportedClaudeCredentialSelection(
            recordedSourceDirectory: "C:\\recorded-claude-config",
            recordedSourceIsExplicit: true,
            WorkerSandboxProvider.Codex,
            sandboxLowIntegrity: true,
            environmentReader: refuseSelectionInput));

        Assert.Null(DispatchProcessHost.TransportedClaudeCredentialSelection(
            recordedSourceDirectory: "C:\\recorded-claude-config",
            recordedSourceIsExplicit: true,
            WorkerSandboxProvider.Claude,
            sandboxLowIntegrity: false,
            environmentReader: refuseSelectionInput));
    }

    private static DispatchProcessHost.DispatchRunParameters CreateClaudeDispatchParameters(
        string root,
        ClaudeCredentialSourceSelection? selection) =>
        new(
            "Write-Output ok",
            root,
            Path.Combine(root, "out.log"),
            Path.Combine(root, "err.log"),
            Path.Combine(root, "exit.txt"),
            null,
            DisableSharedCompilation: false,
            SandboxLowIntegrity: true,
            Provider: WorkerSandboxProvider.Claude,
            ClaudeCredentialSelection: selection);

    /// <summary>
    /// Counts every credential-selection input read. A second, independent resolution cannot hide from
    /// this: any recomputed selection has to read CLAUDE_CONFIG_DIR (and, when it is blank, the default
    /// home root) again. ANTHROPIC_API_KEY is counted separately because it is an auth-mode input, not
    /// a selection input, and both consumers legitimately read it.
    /// </summary>
    private sealed class CountingSelectionInputs
    {
        private readonly string? _configDirectory;
        private readonly string? _defaultHome;

        internal CountingSelectionInputs(string? configDirectory, string? defaultHome)
        {
            _configDirectory = configDirectory;
            _defaultHome = defaultHome;
        }

        internal int ConfigDirectoryReads { get; private set; }

        internal int DefaultHomeReads { get; private set; }

        internal int ApiKeyReads { get; private set; }

        internal Func<string, string?> EnvironmentReader => name =>
        {
            switch (name)
            {
                case "CLAUDE_CONFIG_DIR":
                    ConfigDirectoryReads++;
                    return _configDirectory;
                case "ANTHROPIC_API_KEY":
                    ApiKeyReads++;
                    return null;
                default:
                    return null;
            }
        };

        internal Func<string?> DefaultHomeProvider => () =>
        {
            DefaultHomeReads++;
            return _defaultHome;
        };
    }

    private static void AssertSentinelIsNeverEmitted(Func<string, string> credentialsFactory)
    {
        var sentinel = "REDACTION-SENTINEL-" + Guid.NewGuid().ToString("n");
        var failure = CaptureSeedFailure(directory =>
            WriteCredentialFile(directory, credentialsFactory(sentinel)));

        // Hollow-pass guard: the sentinel must really be in the material that was read, otherwise
        // "absent from diagnostics" proves nothing.
        Assert.Contains(sentinel, failure.SourceCredentialText);

        Assert.DoesNotContain(sentinel, failure.Message);
        Assert.DoesNotContain(sentinel, failure.ExceptionText);
        Assert.DoesNotContain(sentinel, failure.Diagnostics);
        Assert.All(failure.Findings, finding => Assert.DoesNotContain(sentinel, finding));

        // The preflight view of the same material must be redacted too. Record ToString sweeps every
        // property, so a future field carrying bytes would trip here.
        Assert.False(failure.PreflightAuth.HasCliCredentialArtifact);
        Assert.DoesNotContain(sentinel, failure.PreflightAuth.UnavailableReason ?? string.Empty);
        Assert.DoesNotContain(sentinel, failure.PreflightAuth.ToString());
    }

    /// <summary>
    /// Seeds a fixture source shaped by <paramref name="prepareSource"/>, drives the real dispatch
    /// seeding entry point, and returns the typed pre-launch failure alongside the preflight view of
    /// the same source. Everything the caller needs is captured while the fixture is still alive, so
    /// no assertion depends on a deleted temp tree. Also pins the two invariants every
    /// unusable-source case shares: no worker environment is published and no destination artifact
    /// is created.
    /// </summary>
    private static SeedFailure CaptureSeedFailure(Action<string> prepareSource) =>
        CaptureSeedFailureHoldingSource(directory =>
        {
            prepareSource(directory);
            return null;
        });

    /// <summary>
    /// <see cref="CaptureSeedFailure(Action{string})"/> for fixtures that must keep an OS handle open
    /// across the seeding call. The handle is disposed before fixture cleanup.
    /// </summary>
    private static SeedFailure CaptureSeedFailureHoldingSource(Func<string, IDisposable?> prepareSource)
    {
        var root = CreateFixtureRoot();
        IDisposable? sourceHandle = null;
        try
        {
            var sourceDir = Path.Combine(root, "operator-config");
            sourceHandle = prepareSource(sourceDir);

            var startInfo = CreateStartInfo(root);
            var sandboxRoot = Path.Combine(root, ".mcg-sandbox");
            var stderrPath = Path.Combine(root, "dispatch.stderr.log");

            var failure = Assert.Throws<WorkerSubscriptionPreflightException>(() =>
                DispatchProcessHost.SeedProviderEnvironment(
                    startInfo,
                    WorkerSandboxProvider.Claude,
                    sandboxRoot,
                    stderrPath,
                    anthropicApiKeyAccessor: () => null,
                    claudeCredentialDirectoryAccessor: () => sourceDir));

            Assert.False(startInfo.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
            Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "claude-config")));

            return new SeedFailure(
                failure.Message,
                failure.ToString(),
                failure.ErrorCode,
                failure.Findings.ToArray(),
                ReadDiagnostics(stderrPath),
                sourceDir,
                ReadSourceCredentialText(sourceDir),
                ClaudeCliAuthProbe.From(
                    ClaudeEnvironment(configDirectory: sourceDir),
                    () => Path.Combine(root, "unused-home")));
        }
        finally
        {
            sourceHandle?.Dispose();
            DeleteQuietly(root);
        }
    }

    /// <summary>
    /// Best-effort read of the fixture's own credential text. Absent or locked fixtures yield an
    /// empty string, which fails the sentinel test's hollow-pass guard loudly rather than silently.
    /// </summary>
    private static string ReadSourceCredentialText(string sourceDirectory)
    {
        try
        {
            return File.ReadAllText(Path.Combine(sourceDirectory, ".credentials.json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private sealed record SeedFailure(
        string Message,
        string ExceptionText,
        string? ErrorCode,
        string[] Findings,
        string Diagnostics,
        string SourceDirectory,
        string SourceCredentialText,
        ClaudeCliAuthState PreflightAuth);

    private static void WriteCredentialFile(string directory, string credentialsJson)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ".credentials.json"), credentialsJson);
    }

    /// <summary>Relative path plus content hash, so equality proves bytes without printing them.</summary>
    private static string[] SnapshotDirectory(string directory) => Directory
        .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Select(file => Path.GetRelativePath(directory, file) + "=" + HashFile(file))
        .OrderBy(entry => entry, StringComparer.Ordinal)
        .ToArray();

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static bool PathsMatch(string left, string right) => string.Equals(
        left,
        right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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

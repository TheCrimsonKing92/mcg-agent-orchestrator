using System;
using System.IO;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Why a candidate Claude CLI credential source is, or is not, usable local login material.
/// </summary>
internal enum ClaudeCredentialStatus
{
    SourceUnresolved,
    DirectoryMissing,
    CredentialFileMissing,
    CredentialFileEmpty,
    CredentialFileUnreadable,
    CredentialFileMalformed,
    OAuthAccessTokenMissingOrEmpty,
    LocalMaterialPresent,
}

/// <summary>
/// Selection + validation outcome shared by Claude auth preflight and Claude sandbox seeding.
/// Carries locations and a reason code only: credential bytes are never exposed here (no property,
/// no ToString output, no diagnostic text).
/// </summary>
internal sealed record ClaudeCredentialInspection(
    string? DirectoryPath,
    string? CredentialFilePath,
    bool IsExplicitSource,
    ClaudeCredentialStatus Status)
{
    /// <summary>
    /// LOCAL MATERIAL ONLY: a credential file holding a non-empty OAuth access token exists on disk.
    /// This is never proof that the stored session is unexpired or accepted by the service.
    /// </summary>
    internal bool HasLocalAuthMaterial => Status == ClaudeCredentialStatus.LocalMaterialPresent;
}

/// <summary>
/// Preflight's view of the ONE credential source <see cref="ClaudeCredentialSource"/> selected.
/// <paramref name="SelectedSourceDirectory"/>, <paramref name="IsExplicitSource"/> and
/// <paramref name="UnavailableReason"/> carry paths and sanitized reason phrases only - never token,
/// refresh-token or API-key material, and never raw credential bytes.
/// </summary>
public sealed record ClaudeCliAuthState(
    bool HasAnthropicApiKey,
    bool HasCliCredentialArtifact,
    string? CredentialArtifactPath,
    string? SelectedSourceDirectory = null,
    bool IsExplicitSource = false,
    string? UnavailableReason = null);

/// <summary>
/// Auth preflight's entry point into the shared credential contract. It lives beside
/// <see cref="ClaudeCredentialSource"/> rather than beside the dispatcher so the selection rule,
/// its validation and both of its consumers' views stay in one file and cannot drift apart.
/// </summary>
public static class ClaudeCliAuthProbe
{
    public const string AuthUnavailableErrorCode = "ERR_CLAUDE_AUTH_UNAVAILABLE";

    public static ClaudeCliAuthState FromEnvironment() =>
        From(environmentReader: null, defaultHomeProvider: null);

    /// <summary>
    /// Shares one selection and validation rule with worker sandbox seeding
    /// (<see cref="ClaudeCredentialSource"/>): a non-blank CLAUDE_CONFIG_DIR is authoritative and
    /// never falls back to another profile, and a login artifact counts only when
    /// <c>.credentials.json</c> holds a non-empty OAuth access token. Preferences, settings and
    /// config files alone are not login material.
    /// A positive result is LOCAL MATERIAL presence only; it is never proof of a live session, and
    /// expiry and service acceptance are not evaluated here.
    /// Environment and default-home inputs are injectable so tests stay deterministic without
    /// mutating process-global environment state.
    /// </summary>
    internal static ClaudeCliAuthState From(
        Func<string, string?>? environmentReader,
        Func<string?>? defaultHomeProvider)
    {
        var read = environmentReader ?? ClaudeCredentialSource.ProcessEnvironmentReader;
        var hasApiKey = !string.IsNullOrWhiteSpace(read(ClaudeCredentialSource.ApiKeyEnvironmentVariable));
        var inspection = ClaudeCredentialSource.Inspect(read, defaultHomeProvider);

        // The selected directory is reported UNCONDITIONALLY, including when nothing usable was
        // found: without it an operator cannot tell which login source preflight rejected, which is
        // exactly how preflight and sandbox seeding drifted apart unnoticed.
        return new ClaudeCliAuthState(
            hasApiKey,
            inspection.HasLocalAuthMaterial,
            inspection.HasLocalAuthMaterial ? inspection.CredentialFilePath : null,
            inspection.DirectoryPath,
            inspection.IsExplicitSource,
            inspection.HasLocalAuthMaterial
                ? null
                : ClaudeCredentialSource.DescribeRejectedSource(inspection));
    }
}

/// <summary>
/// The one place that decides which Claude CLI login is authoritative and whether it is usable.
/// Auth preflight and worker sandbox seeding both go through here so they can never disagree.
/// Selection is deliberately narrow: a non-blank CLAUDE_CONFIG_DIR is authoritative and never falls
/// back to another profile; otherwise the default home profile directory is used. Environment and
/// default-home inputs are injectable so tests are deterministic without mutating process state.
/// </summary>
internal static class ClaudeCredentialSource
{
    internal const string ConfigDirectoryEnvironmentVariable = "CLAUDE_CONFIG_DIR";
    internal const string ApiKeyEnvironmentVariable = "ANTHROPIC_API_KEY";
    internal const string CredentialsFileName = ".credentials.json";
    internal const string SettingsFileName = "settings.json";

    internal static readonly Func<string, string?> ProcessEnvironmentReader =
        static name => Environment.GetEnvironmentVariable(name);

    /// <summary>Preflight view of the authoritative source: locations and reason only, no bytes.</summary>
    internal static ClaudeCredentialInspection Inspect(
        Func<string, string?>? environmentReader = null,
        Func<string?>? defaultHomeProvider = null)
    {
        var (directoryPath, isExplicitSource) = ResolveLocation(environmentReader, defaultHomeProvider);
        return Load(directoryPath, isExplicitSource).Inspection;
    }

    /// <summary>
    /// Validates the operator's Claude CLI login source and seeds the validated payload into the
    /// worker sandbox config root (claude-cli reads credentials from the ROOT of CLAUDE_CONFIG_DIR).
    /// Throws <see cref="WorkerSubscriptionPreflightException"/> carrying
    /// <see cref="ClaudeCliAuthProbe.AuthUnavailableErrorCode"/> and a sanitized diagnostic BEFORE
    /// creating, overwriting or deleting any destination artifact when the source is unresolved,
    /// missing, unreadable, malformed or empty. Copy failures surface the same way and are never
    /// swallowed into a successful launch. API-key mode never reaches this method.
    /// </summary>
    internal static void SeedSubscriptionCredentials(
        string destinationDirectory,
        Func<string>? credentialDirectoryAccessor = null,
        Func<string, string?>? environmentReader = null,
        Func<string?>? defaultHomeProvider = null,
        Action<string>? diagnosticSink = null)
    {
        // The existing injected-directory seam stays supported and counts as operator-explicit.
        var (directoryPath, isExplicitSource) = credentialDirectoryAccessor is not null
            ? (credentialDirectoryAccessor(), true)
            : ResolveLocation(environmentReader, defaultHomeProvider);

        // Material validation happens FIRST. No shortcut (including source == destination) may let
        // unusable material through, and nothing in the destination is touched until it passes.
        var (inspection, payload) = Load(directoryPath, isExplicitSource);
        if (payload is null)
        {
            throw Unavailable(inspection, diagnosticSink);
        }

        if (IsSameLocation(inspection.DirectoryPath, destinationDirectory))
        {
            // The validated source already IS the sandbox config root: nothing to copy and nothing
            // to rewrite, so its bytes stay exactly as validated.
            return;
        }

        try
        {
            Directory.CreateDirectory(destinationDirectory);

            // Seed the bytes that were just validated, never a second read of the file.
            File.WriteAllBytes(Path.Combine(destinationDirectory, CredentialsFileName), payload);

            var sourceSettings = Path.Combine(inspection.DirectoryPath!, SettingsFileName);
            if (File.Exists(sourceSettings))
            {
                File.Copy(
                    sourceSettings,
                    Path.Combine(destinationDirectory, SettingsFileName),
                    overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Unavailable(
                inspection,
                diagnosticSink,
                "copying the validated login into the worker sandbox failed (" + ex.GetType().Name + ")");
        }
    }

    /// <summary>
    /// Ensures the sandbox config root has a settings file. Never overwrites an existing one, so a
    /// seeded operator settings file wins.
    /// </summary>
    internal static void EnsureSandboxSettings(string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var settingsPath = Path.Combine(destinationDirectory, SettingsFileName);
        if (!File.Exists(settingsPath))
        {
            File.WriteAllText(settingsPath, "{}\n");
        }
    }

    private static (string? DirectoryPath, bool IsExplicitSource) ResolveLocation(
        Func<string, string?>? environmentReader,
        Func<string?>? defaultHomeProvider)
    {
        var read = environmentReader ?? ProcessEnvironmentReader;

        var configured = read(ConfigDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // Authoritative: if this is set but unusable, that is a failure, not a reason to pick a
            // different profile.
            return (configured, true);
        }

        var home = defaultHomeProvider is not null ? defaultHomeProvider() : DefaultHome(read);
        return string.IsNullOrWhiteSpace(home)
            ? (null, false)
            : (Path.Combine(home.Trim(), ".claude"), false);
    }

    private static string? DefaultHome(Func<string, string?> read)
    {
        string? profile = null;
        try
        {
            profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        catch (PlatformNotSupportedException)
        {
        }

        if (!string.IsNullOrWhiteSpace(profile))
        {
            return profile;
        }

        var userProfile = read("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return userProfile;
        }

        var home = read("HOME");
        return string.IsNullOrWhiteSpace(home) ? null : home;
    }

    /// <summary>
    /// Reads and validates the candidate credential payload exactly once. The returned bytes ARE the
    /// validated payload; they stay local to this type and are only ever written to the sandbox.
    /// </summary>
    private static (ClaudeCredentialInspection Inspection, byte[]? Payload) Load(
        string? directoryPath,
        bool isExplicitSource)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return (new(null, null, isExplicitSource, ClaudeCredentialStatus.SourceUnresolved), null);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(directoryPath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (new(directoryPath, null, isExplicitSource, ClaudeCredentialStatus.SourceUnresolved), null);
        }

        if (!Directory.Exists(fullPath))
        {
            return (new(fullPath, null, isExplicitSource, ClaudeCredentialStatus.DirectoryMissing), null);
        }

        var credentialPath = Path.Combine(fullPath, CredentialsFileName);
        if (!File.Exists(credentialPath))
        {
            return (new(fullPath, null, isExplicitSource, ClaudeCredentialStatus.CredentialFileMissing), null);
        }

        byte[] payload;
        try
        {
            payload = File.ReadAllBytes(credentialPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (
                new(fullPath, credentialPath, isExplicitSource, ClaudeCredentialStatus.CredentialFileUnreadable),
                null);
        }

        var status = Evaluate(payload);
        return (
            new(fullPath, credentialPath, isExplicitSource, status),
            status == ClaudeCredentialStatus.LocalMaterialPresent ? payload : null);
    }

    /// <summary>
    /// Content-shape check only: the known OAuth access token must be present and non-empty. This
    /// never contacts the service, and it does not evaluate expiry or refresh-token usability.
    /// </summary>
    private static ClaudeCredentialStatus Evaluate(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return ClaudeCredentialStatus.CredentialFileEmpty;
        }

        try
        {
            using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(payload));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ClaudeCredentialStatus.CredentialFileMalformed;
            }

            if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                oauth.ValueKind != JsonValueKind.Object ||
                !oauth.TryGetProperty("accessToken", out var token) ||
                token.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(token.GetString()))
            {
                return ClaudeCredentialStatus.OAuthAccessTokenMissingOrEmpty;
            }

            return ClaudeCredentialStatus.LocalMaterialPresent;
        }
        catch (JsonException)
        {
            return ClaudeCredentialStatus.CredentialFileMalformed;
        }
    }

    /// <summary>
    /// Builds the sanitized failure diagnostic (source kind, directory path and reason phrase only -
    /// never a token value and never raw credential JSON), publishes it to the optional sink, and
    /// returns the typed preflight failure for the caller to throw.
    /// </summary>
    private static WorkerSubscriptionPreflightException Unavailable(
        ClaudeCredentialInspection inspection,
        Action<string>? diagnosticSink,
        string? reasonOverride = null)
    {
        var diagnostic =
            ClaudeCliAuthProbe.AuthUnavailableErrorCode +
            ": Claude worker dispatch stopped before launch. " +
            ApiKeyEnvironmentVariable + " is not set and the " +
            DescribeSource(inspection) + " is unusable: " +
            (reasonOverride ?? Describe(inspection.Status)) +
            ". Repair the selected login source before retrying dispatch.";

        diagnosticSink?.Invoke(diagnostic);

        return new WorkerSubscriptionPreflightException(
            diagnostic,
            ClaudeCliAuthProbe.AuthUnavailableErrorCode,
            new[] { diagnostic });
    }

    /// <summary>
    /// Sanitized preflight summary of the one source that was attempted: source kind, directory path
    /// and reason phrase only - never a token value, never raw credential JSON, never exception text.
    /// Auth preflight reports this so the source it names is provably the source seeding would use.
    /// </summary>
    /// <remarks>
    /// The candidate list is exactly ONE entry by contract, not by omission: a non-blank
    /// CLAUDE_CONFIG_DIR is authoritative and must never fall back to another profile, and when it is
    /// blank the default profile is the only remaining candidate. Do not re-add fallback enumeration
    /// to make this list longer - falling through would silently seed a different account's login.
    /// </remarks>
    internal static string DescribeRejectedSource(ClaudeCredentialInspection inspection) =>
        "sole candidate " + DescribeSource(inspection) + " was rejected because " +
        Describe(inspection.Status);

    private static string DescribeSource(ClaudeCredentialInspection inspection) =>
        (inspection.IsExplicitSource
            ? "explicit " + ConfigDirectoryEnvironmentVariable + " login source"
            : "default profile login source") +
        " '" + (inspection.DirectoryPath ?? "<unresolved>") + "'";

    private static string Describe(ClaudeCredentialStatus status) => status switch
    {
        ClaudeCredentialStatus.SourceUnresolved => "no credential directory could be resolved",
        ClaudeCredentialStatus.DirectoryMissing => "the directory does not exist",
        ClaudeCredentialStatus.CredentialFileMissing =>
            CredentialsFileName +
            " is absent (preferences, settings and config files alone are not login material)",
        ClaudeCredentialStatus.CredentialFileEmpty => CredentialsFileName + " is empty",
        ClaudeCredentialStatus.CredentialFileUnreadable => CredentialsFileName + " could not be read",
        ClaudeCredentialStatus.CredentialFileMalformed =>
            CredentialsFileName + " is not a readable JSON object",
        ClaudeCredentialStatus.OAuthAccessTokenMissingOrEmpty =>
            CredentialsFileName + " has no non-empty OAuth access token",
        ClaudeCredentialStatus.LocalMaterialPresent => "local login material is present",
        _ => "unknown credential source state",
    };

    /// <summary>
    /// True when two directory paths denote the same location, normalizing trailing separators and
    /// (on Windows) case, so seeding never copies a directory onto itself.
    /// </summary>
    private static bool IsSameLocation(string? left, string? right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        if (normalizedLeft is null || normalizedRight is null)
        {
            return false;
        }

        return string.Equals(
            normalizedLeft,
            normalizedRight,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

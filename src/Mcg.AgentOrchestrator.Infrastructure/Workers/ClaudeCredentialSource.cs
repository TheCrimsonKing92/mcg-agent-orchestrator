using System;
using System.IO;
using System.Text.Json;
using System.Threading;

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
/// Preflight's view of the ONE credential source <see cref="ClaudeCredentialResolver"/> selected.
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
    string? UnavailableReason = null)
{
    /// <summary>
    /// The ONE resolved result this view was built from, carried so the next consumer in the same
    /// process (sandbox seeding) consumes THIS resolution instead of recomputing selection. Null only
    /// for a hand-built state, such as an injected test probe.
    /// Internal on purpose: an internal member is excluded from the record's generated printer, so the
    /// resolution - and the validated credential bytes it holds - can never reach ToString output.
    /// </summary>
    internal ClaudeCredentialResolution? Resolution { get; init; }
}

/// <summary>
/// Auth preflight's entry point into the shared credential contract. It lives beside
/// <see cref="ClaudeCredentialResolver"/> rather than beside the dispatcher so the selection rule,
/// its validation and both of its consumers' views stay in one file and cannot drift apart.
/// </summary>
public static class ClaudeCliAuthProbe
{
    public const string AuthUnavailableErrorCode = "ERR_CLAUDE_AUTH_UNAVAILABLE";

    public static ClaudeCliAuthState FromEnvironment() =>
        From(environmentReader: null, defaultHomeProvider: null);

    /// <summary>
    /// Convenience overload for a caller that owns no resolver yet: it creates one, which means this
    /// call IS that resolver's single resolution. A caller that must hand the same resolved result to
    /// sandbox seeding has to create the resolver itself and use <see cref="From(ClaudeCredentialResolver)"/>.
    /// </summary>
    internal static ClaudeCliAuthState From(
        Func<string, string?>? environmentReader,
        Func<string?>? defaultHomeProvider) =>
        From(new ClaudeCredentialResolver(environmentReader, defaultHomeProvider));

    /// <summary>
    /// Builds preflight's view FROM the resolver's single resolved result and carries that result on
    /// the returned state, so seeding sharing this resolver consumes the same object rather than
    /// recomputing selection.
    /// The rule is the resolver's, not preflight's: a non-blank CLAUDE_CONFIG_DIR is authoritative and
    /// never falls back to another profile, and a login artifact counts only when
    /// <c>.credentials.json</c> holds a non-empty OAuth access token. Preferences, settings and config
    /// files alone are not login material.
    /// A positive result is LOCAL MATERIAL presence only; it is never proof of a live session, and
    /// expiry and service acceptance are not evaluated here.
    /// </summary>
    internal static ClaudeCliAuthState From(ClaudeCredentialResolver resolver)
    {
        var hasApiKey = !string.IsNullOrWhiteSpace(
            resolver.ReadEnvironment(ClaudeCredentialSource.ApiKeyEnvironmentVariable));
        var resolution = resolver.Resolve();
        var inspection = resolution.Inspection;

        // The selected directory is reported UNCONDITIONALLY, including when nothing usable was
        // found: without it an operator cannot tell which login source preflight rejected, which is
        // exactly how preflight and sandbox seeding drifted apart unnoticed.
        return new ClaudeCliAuthState(
            hasApiKey,
            resolution.HasLocalAuthMaterial,
            resolution.HasLocalAuthMaterial ? inspection.CredentialFilePath : null,
            inspection.DirectoryPath,
            inspection.IsExplicitSource,
            resolution.HasLocalAuthMaterial
                ? null
                : ClaudeCredentialSource.DescribeRejectedSource(inspection))
        {
            Resolution = resolution,
        };
    }
}

/// <summary>
/// ONE resolved credential source: the selection outcome plus the credential payload that selection
/// validated. Produced exactly once by <see cref="ClaudeCredentialResolver"/> and then handed to every
/// consumer, so preflight's reported source and the bytes seeding writes come from the same decision.
/// Deliberately NOT a record: no compiler-generated printer or equality member may ever reach
/// <see cref="ValidatedPayload"/>.
/// </summary>
internal sealed class ClaudeCredentialResolution
{
    internal ClaudeCredentialResolution(ClaudeCredentialInspection inspection, byte[]? validatedPayload)
    {
        Inspection = inspection;
        ValidatedPayload = validatedPayload;
    }

    internal ClaudeCredentialInspection Inspection { get; }

    /// <summary>
    /// The bytes selection read and validated, or null when the source is unusable. They are written
    /// only into a sandbox config directory and never appear in a diagnostic, log or exception.
    /// </summary>
    internal byte[]? ValidatedPayload { get; }

    internal bool HasLocalAuthMaterial => Inspection.HasLocalAuthMaterial;

    /// <summary>Path and status only, so an accidental interpolation cannot print credential bytes.</summary>
    public override string ToString() =>
        nameof(ClaudeCredentialResolution) + "(" +
        (Inspection.DirectoryPath ?? "<unresolved>") + ", " + Inspection.Status + ")";
}

/// <summary>
/// The one place that decides which Claude CLI login is authoritative and whether it is usable:
/// ONE resolver instance is ONE resolution. The first <see cref="Resolve"/> performs the only
/// selection and the only credential read; every later caller receives that same
/// <see cref="ClaudeCredentialResolution"/> instance. Auth preflight and worker sandbox seeding share
/// a resolver, so the source preflight reports is the object seeding consumes - not a second
/// computation that can disagree with it.
/// Selection is deliberately narrow: a non-blank CLAUDE_CONFIG_DIR is authoritative and never falls
/// back to another profile; otherwise the default home profile directory is used. Environment and
/// default-home inputs are injectable so tests are deterministic without mutating process state.
/// </summary>
/// <remarks>
/// PROCESS SCOPE: the conductor resolves during dispatch preflight, while seeding runs in the detached
/// dispatch host (<c>DispatchProcessHost.Run</c>) - a separate process that cannot share an object
/// with its parent. Within each process the transported resolution is the only decision; across the
/// boundary this type, its single-candidate rule and its injected inputs are the only decider, so the
/// two processes cannot select different sources. Do not "reconcile" that by re-resolving inside a
/// consumer: recomputation is exactly the drift this type exists to remove.
/// </remarks>
internal sealed class ClaudeCredentialResolver
{
    private readonly Func<string, string?> _environmentReader;
    private readonly Func<string?>? _defaultHomeProvider;
    private readonly Func<string>? _explicitDirectoryAccessor;
    private readonly object _gate = new();
    private ClaudeCredentialResolution? _resolution;
    private int _resolutionCount;

    /// <param name="environmentReader">Reads CLAUDE_CONFIG_DIR and ANTHROPIC_API_KEY; process env by default.</param>
    /// <param name="defaultHomeProvider">Supplies the home root for the fallback candidate.</param>
    /// <param name="explicitDirectoryAccessor">
    /// Pre-selected source directory seam (an operator-explicit override supplied by the caller). When
    /// present it replaces candidate evaluation and counts as explicit, so it never falls through to
    /// the default profile either.
    /// </param>
    internal ClaudeCredentialResolver(
        Func<string, string?>? environmentReader = null,
        Func<string?>? defaultHomeProvider = null,
        Func<string>? explicitDirectoryAccessor = null)
    {
        _environmentReader = environmentReader ?? ClaudeCredentialSource.ProcessEnvironmentReader;
        _defaultHomeProvider = defaultHomeProvider;
        _explicitDirectoryAccessor = explicitDirectoryAccessor;
    }

    /// <summary>
    /// How many times selection actually ran. Consumers sharing one resolver must leave this at 1: a
    /// second computation is the preflight/seeding disagreement this type exists to prevent, so tests
    /// assert it rather than trusting two results to look alike.
    /// </summary>
    internal int ResolutionCount => Volatile.Read(ref _resolutionCount);

    /// <summary>
    /// The resolver's own environment view, so a consumer that also needs ANTHROPIC_API_KEY reads it
    /// through the same injected reader instead of reaching for process state.
    /// </summary>
    internal string? ReadEnvironment(string name) => _environmentReader(name);

    /// <summary>
    /// Returns this resolver's single resolved result, computing it on first call and returning the
    /// identical instance afterwards.
    /// </summary>
    internal ClaudeCredentialResolution Resolve()
    {
        if (Volatile.Read(ref _resolution) is { } resolved)
        {
            return resolved;
        }

        lock (_gate)
        {
            if (_resolution is { } existing)
            {
                return existing;
            }

            var (directoryPath, isExplicitSource) = _explicitDirectoryAccessor is not null
                ? (_explicitDirectoryAccessor(), true)
                : ClaudeCredentialSource.ResolveLocation(_environmentReader, _defaultHomeProvider);

            _resolutionCount++;
            var resolution = ClaudeCredentialSource.Load(directoryPath, isExplicitSource);
            Volatile.Write(ref _resolution, resolution);
            return resolution;
        }
    }
}

/// <summary>
/// Mechanics of the shared credential contract: candidate evaluation, material validation, sandbox
/// seeding and the sanitized diagnostics. Only <see cref="ClaudeCredentialResolver"/> may call
/// <see cref="ResolveLocation"/> or <see cref="Load"/> - going around it is how a consumer would
/// reintroduce a second, disagreeing selection.
/// </summary>
internal static class ClaudeCredentialSource
{
    internal const string ConfigDirectoryEnvironmentVariable = "CLAUDE_CONFIG_DIR";
    internal const string ApiKeyEnvironmentVariable = "ANTHROPIC_API_KEY";
    internal const string CredentialsFileName = ".credentials.json";
    internal const string SettingsFileName = "settings.json";

    internal static readonly Func<string, string?> ProcessEnvironmentReader =
        static name => Environment.GetEnvironmentVariable(name);

    /// <summary>
    /// Seeds the resolver's already-validated payload into the worker sandbox config root (claude-cli
    /// reads credentials from the ROOT of CLAUDE_CONFIG_DIR) and returns the resolved result it
    /// consumed, so a caller can report exactly which source was seeded.
    /// Selection is NOT repeated here: the result comes from <paramref name="resolver"/>, which is the
    /// same object auth preflight reported when both share a resolver.
    /// Throws <see cref="WorkerSubscriptionPreflightException"/> carrying
    /// <see cref="ClaudeCliAuthProbe.AuthUnavailableErrorCode"/> and a sanitized diagnostic BEFORE
    /// creating, overwriting or deleting any destination artifact when the source is unresolved,
    /// missing, unreadable, malformed or empty. Copy failures surface the same way and are never
    /// swallowed into a successful launch. API-key mode never reaches this method.
    /// </summary>
    internal static ClaudeCredentialResolution SeedSubscriptionCredentials(
        string destinationDirectory,
        ClaudeCredentialResolver resolver,
        Action<string>? diagnosticSink = null)
    {
        // The ONE resolved result, transported rather than recomputed.
        var resolution = resolver.Resolve();
        var inspection = resolution.Inspection;

        // Material validation already happened during that resolution. No shortcut (including
        // source == destination) may let unusable material through, and nothing in the destination is
        // touched until it passes.
        if (resolution.ValidatedPayload is not { } payload)
        {
            throw Unavailable(inspection, diagnosticSink);
        }

        if (IsSameLocation(inspection.DirectoryPath, destinationDirectory))
        {
            // The validated source already IS the sandbox config root: nothing to copy and nothing
            // to rewrite, so its bytes stay exactly as validated.
            return resolution;
        }

        try
        {
            Directory.CreateDirectory(destinationDirectory);

            // Seed the bytes that were validated during resolution, never a second read of the file.
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

        return resolution;
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

    /// <summary>
    /// Candidate evaluation. Call only from <see cref="ClaudeCredentialResolver.Resolve"/>, which owns
    /// the single resolution every consumer shares.
    /// </summary>
    internal static (string? DirectoryPath, bool IsExplicitSource) ResolveLocation(
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
    /// Reads and validates the selected candidate exactly once and returns the resolved result every
    /// consumer then shares. The carried bytes ARE the validated payload; they stay inside the
    /// resolution and are only ever written to a sandbox config directory.
    /// Call only from <see cref="ClaudeCredentialResolver.Resolve"/>.
    /// </summary>
    internal static ClaudeCredentialResolution Load(string? directoryPath, bool isExplicitSource)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return new(new(null, null, isExplicitSource, ClaudeCredentialStatus.SourceUnresolved), null);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(directoryPath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(new(directoryPath, null, isExplicitSource, ClaudeCredentialStatus.SourceUnresolved), null);
        }

        if (!Directory.Exists(fullPath))
        {
            return new(new(fullPath, null, isExplicitSource, ClaudeCredentialStatus.DirectoryMissing), null);
        }

        var credentialPath = Path.Combine(fullPath, CredentialsFileName);
        if (!File.Exists(credentialPath))
        {
            return new(new(fullPath, null, isExplicitSource, ClaudeCredentialStatus.CredentialFileMissing), null);
        }

        byte[] payload;
        try
        {
            payload = File.ReadAllBytes(credentialPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(
                new(fullPath, credentialPath, isExplicitSource, ClaudeCredentialStatus.CredentialFileUnreadable),
                null);
        }

        var status = Evaluate(payload);
        return new(
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

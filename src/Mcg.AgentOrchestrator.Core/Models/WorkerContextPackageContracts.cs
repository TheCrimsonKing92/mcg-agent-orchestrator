using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Core;

public enum ContextArtifactKind
{
    TaskObjective,
    AcceptanceCriteria,
    RoleOutputContract,
    OperatorInstructions,
    ContextManifest,
    ResearcherEvidence,
    PlannerPlan,
    PriorTaskEvidence,
    RegisteredContext
}

public enum ContextDeliveryMode
{
    InlineFull,
    MandatoryFile,
    OnDemandFile,
    HistoricalFile
}

public readonly record struct ContextContractVersion(int Value)
{
    public static ContextContractVersion V1 { get; } = new(1);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct LogicalArtifactIdentity
{
    public LogicalArtifactIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Normalize(NormalizationForm.FormC);
        if (!string.Equals(value, normalized, StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal) ||
            value.StartsWith("/", StringComparison.Ordinal) ||
            value.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("Logical artifact identity must be NFC, root-relative, and use forward slashes.", nameof(value));
        }

        var segments = value.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException("Logical artifact identity contains an empty or dot segment.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public static class WorkerContextProjectionBoundary
{
    public const string StartPrefix = "<!-- WORKER_CONTEXT_TYPED_PROJECTION_START:";
    public const string EndPrefix = "<!-- WORKER_CONTEXT_TYPED_PROJECTION_END:";
    public const string LiteralPrefix = "<!-- WORKER_CONTEXT_TYPED_PROJECTION_LITERAL:";
    public const string MarkerSuffix = " -->";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Start(LogicalArtifactIdentity identity) =>
        Format(StartPrefix, identity);

    public static string End(LogicalArtifactIdentity identity) =>
        Format(EndPrefix, identity);

    public static string EscapeReservedLiteral(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!line.Contains(StartPrefix, StringComparison.Ordinal) &&
            !line.Contains(EndPrefix, StringComparison.Ordinal) &&
            !line.Contains(LiteralPrefix, StringComparison.Ordinal))
        {
            return line;
        }

        return $"{LiteralPrefix}{Convert.ToBase64String(StrictUtf8.GetBytes(line))}{MarkerSuffix}";
    }

    public static string RestoreReservedLiteral(string marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        if (!marker.StartsWith(LiteralPrefix, StringComparison.Ordinal) ||
            !marker.EndsWith(MarkerSuffix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Typed projection literal marker is malformed.", nameof(marker));
        }

        var encoded = marker[LiteralPrefix.Length..^MarkerSuffix.Length];
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (!string.Equals(Convert.ToBase64String(bytes), encoded, StringComparison.Ordinal))
            {
                throw new ArgumentException("Typed projection literal marker is not canonical base64.", nameof(marker));
            }

            return StrictUtf8.GetString(bytes);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new ArgumentException("Typed projection literal marker is malformed.", nameof(marker), exception);
        }
    }

    private static string Format(string prefix, LogicalArtifactIdentity identity)
    {
        if (identity.Value.Contains("<!--", StringComparison.Ordinal) ||
            identity.Value.Contains("-->", StringComparison.Ordinal) ||
            identity.Value.Contains('\r', StringComparison.Ordinal) ||
            identity.Value.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("Typed projection identity cannot contain Markdown comment or line boundaries.", nameof(identity));
        }

        return $"{prefix}{identity.Value}{MarkerSuffix}";
    }
}

public sealed class WorkerContextArtifact
{
    private readonly byte[]? _authoritativeBytes;
    private static readonly AgentRole[] CanonicalRoleOrder =
    [
        AgentRole.Researcher,
        AgentRole.Planner,
        AgentRole.Developer,
        AgentRole.Tester,
        AgentRole.Reviewer
    ];

    private WorkerContextArtifact(
        LogicalArtifactIdentity identity,
        ContextArtifactKind kind,
        string contentHash,
        IReadOnlyList<AgentRole> roleVisibility,
        ContextDeliveryMode deliveryMode,
        ContextContractVersion contractVersion,
        byte[]? authoritativeBytes,
        string? mandatoryRelativePath,
        string? fallbackReason,
        int authoritativeByteCount)
    {
        Identity = identity;
        Kind = kind;
        ContentHash = contentHash;
        RoleVisibility = roleVisibility;
        DeliveryMode = deliveryMode;
        ContractVersion = contractVersion;
        _authoritativeBytes = authoritativeBytes?.ToArray();
        MandatoryRelativePath = mandatoryRelativePath;
        FallbackReason = fallbackReason;
        AuthoritativeByteCount = authoritativeByteCount;
    }

    public LogicalArtifactIdentity Identity { get; }
    public ContextArtifactKind Kind { get; }
    public string ContentHash { get; }
    public IReadOnlyList<AgentRole> RoleVisibility { get; }
    public ContextDeliveryMode DeliveryMode { get; }
    public ContextContractVersion ContractVersion { get; }
    public byte[]? AuthoritativeBytes => _authoritativeBytes?.ToArray();
    public string? MandatoryRelativePath { get; }
    public string? FallbackReason { get; }
    public int AuthoritativeByteCount { get; }
    public static WorkerContextArtifact Create(
        LogicalArtifactIdentity identity,
        ContextArtifactKind kind,
        byte[]? authoritativeBytes,
        IEnumerable<AgentRole> roleVisibility,
        ContextDeliveryMode deliveryMode,
        ContextContractVersion contractVersion,
        string? mandatoryRelativePath = null,
        string? expectedContentHash = null,
        string? fallbackReason = null,
        int? authoritativeByteCount = null)
    {
        if (contractVersion.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contractVersion), "Worker context contract versions must be positive.");
        }

        if (!Enum.IsDefined(deliveryMode))
        {
            throw new ArgumentOutOfRangeException(nameof(deliveryMode), deliveryMode, "Worker context delivery mode is undefined.");
        }

        var requestedVisibility = roleVisibility.ToArray();
        var requestedSet = requestedVisibility.ToHashSet();
        var visibility = CanonicalRoleOrder.Where(requestedSet.Contains).ToArray();
        if (visibility.Length == 0 || visibility.Length != requestedVisibility.Distinct().Count())
        {
            throw new ArgumentException("Visibility must be a non-empty allow-list containing only the five worker roles.", nameof(roleVisibility));
        }

        var computedHash = authoritativeBytes is null ? null : Hash(authoritativeBytes);
        var contentHash = expectedContentHash ?? computedHash
            ?? throw new ArgumentException("An expected SHA-256 hash is required when authoritative bytes are unavailable.", nameof(expectedContentHash));
        if (contentHash.Length != 64 || contentHash.Any(character => !Uri.IsHexDigit(character)) ||
            !contentHash.Equals(contentHash.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Content hash must be 64 lowercase hexadecimal SHA-256 characters.", nameof(expectedContentHash));
        }

        if (computedHash is not null && !computedHash.Equals(contentHash, StringComparison.Ordinal))
        {
            throw new ArgumentException("Authoritative bytes do not match the expected SHA-256 hash.", nameof(authoritativeBytes));
        }

        var byteCount = authoritativeByteCount ?? authoritativeBytes?.Length ?? 0;
        if (byteCount < 0 || (authoritativeBytes is not null && byteCount != authoritativeBytes.Length))
        {
            throw new ArgumentOutOfRangeException(
                nameof(authoritativeByteCount),
                "Authoritative byte count must be non-negative and match supplied authoritative bytes.");
        }

        if (deliveryMode == ContextDeliveryMode.InlineFull && authoritativeBytes is null)
        {
            throw new ArgumentException("InlineFull requires complete authoritative bytes.", nameof(authoritativeBytes));
        }

        if (deliveryMode is ContextDeliveryMode.MandatoryFile or ContextDeliveryMode.OnDemandFile or ContextDeliveryMode.HistoricalFile &&
            string.IsNullOrWhiteSpace(mandatoryRelativePath))
        {
            throw new ArgumentException("File-backed delivery requires a relative materialization path.", nameof(mandatoryRelativePath));
        }

        return new WorkerContextArtifact(
            identity,
            kind,
            contentHash,
            visibility,
            deliveryMode,
            contractVersion,
            authoritativeBytes,
            mandatoryRelativePath,
            fallbackReason,
            byteCount);
    }

    public static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed record WorkerContextPackage(
    string SemanticPackageId,
    ContextContractVersion ContractVersion,
    AgentRole TargetRole,
    IReadOnlyList<WorkerContextArtifact> Artifacts,
    ReviewFindingHistoryProjectionMetrics? ReviewFindingProjection = null)
{
    // This is semantic attestation/cache identity only. It is not delivery, read, or acknowledgment evidence.
}

public sealed class WorkerContextPreparationException : InvalidOperationException
{
    public WorkerContextPreparationException(LogicalArtifactIdentity identity, string reason, string detail)
        : base($"Worker context artifact '{identity.Value}' failed preparation ({reason}): {detail}")
    {
        Identity = identity;
        Reason = reason;
    }

    public LogicalArtifactIdentity Identity { get; }
    public string Reason { get; }
}

public enum ProviderUsageState
{
    Reported,
    Unknown
}

public sealed record ProviderUsageValue
{
    public ProviderUsageValue(ProviderUsageState state, long? value, string? unknownReason)
    {
        if (state == ProviderUsageState.Reported && (value is null || value < 0 || unknownReason is not null))
        {
            throw new ArgumentException("Reported provider usage requires a non-negative value and no unknown reason.");
        }

        if (state == ProviderUsageState.Unknown && (value is not null || string.IsNullOrWhiteSpace(unknownReason)))
        {
            throw new ArgumentException("Unknown provider usage requires a reason and cannot contain a value.");
        }

        State = state;
        Value = value;
        UnknownReason = unknownReason;
    }

    public ProviderUsageState State { get; }
    public long? Value { get; }
    public string? UnknownReason { get; }

    public static ProviderUsageValue Reported(long value) => value < 0
        ? throw new ArgumentOutOfRangeException(nameof(value))
        : new(ProviderUsageState.Reported, value, null);

    public static ProviderUsageValue Unknown(string reason) =>
        new(ProviderUsageState.Unknown, null, string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason);
}

public sealed record WorkerContextSectionReceipt(
    string LogicalIdentity,
    int CharacterCount,
    int ByteCount,
    string ContentHash,
    ContextDeliveryMode DeliveryMode,
    int ContractVersion,
    IReadOnlyList<AgentRole> RoleVisibility,
    string? FallbackReason = null,
    string? MandatoryRelativePath = null,
    ContextArtifactKind? ArtifactKind = null);

public sealed record MandatoryContextFileDescriptor(
    string LogicalIdentity,
    string RelativePath,
    string Sha256,
    int ContractVersion,
    AgentRole TargetRole,
    IReadOnlyList<AgentRole> RoleVisibility);

public sealed record WorkerContextPackageReceipt(
    string SemanticPackageId,
    IReadOnlyList<WorkerContextSectionReceipt> Sections,
    ProviderUsageValue InputTokens,
    ProviderUsageValue CachedInputTokens,
    ProviderUsageValue OutputTokens,
    int RenderedPromptBytes = 0,
    ReviewFindingHistoryProjectionMode? ReviewFindingProjectionMode = null,
    int UniqueReviewFindingRoundCount = 0,
    int DuplicateReviewFindingRoundCount = 0,
    int UniqueFindingEvidenceReceiptCount = 0,
    int DuplicateFindingEvidenceReceiptCount = 0,
    string? ReviewFindingFallbackReason = null,
    int RenderedPromptCharacters = 0,
    int DeliveredArtifactBytes = 0,
    int OnDemandArtifactBytes = 0,
    int ToolTranscriptCharacters = 0,
    int ModelInputTokenEstimate = 0,
    bool EarlyConvergenceEligible = false,
    string? EarlyConvergenceCandidateSha = null,
    IReadOnlyList<string>? EarlyConvergenceReceiptHashes = null,
    bool ValidatedForIdempotentReuse = false,
    int BaselinePromptCharacters = 0,
    int BaselineDeliveredArtifactBytes = 0,
    int BaselineToolTranscriptCharacters = 0,
    int BaselineModelInputTokenEstimate = 0)
{
    public WorkerContextPackageReceipt WithProviderUsage(ProviderReportedUsage? usage, string unavailableReason = "absent") => this with
    {
        InputTokens = MergeUsageValue(InputTokens, usage?.InputTokens, unavailableReason),
        CachedInputTokens = MergeUsageValue(CachedInputTokens, usage?.CachedInputTokens, unavailableReason),
        OutputTokens = MergeUsageValue(OutputTokens, usage?.OutputTokens, unavailableReason)
    };

    public WorkerContextPackageReceipt WithProviderUsage(ModelUsage? usage, string unavailableReason = "absent") => this with
    {
        InputTokens = MergeUsageValue(InputTokens, usage?.InputTokens, unavailableReason),
        CachedInputTokens = MergeUsageValue(CachedInputTokens, usage?.CachedInputTokens, unavailableReason),
        OutputTokens = MergeUsageValue(OutputTokens, usage?.OutputTokens, unavailableReason)
    };

    public WorkerContextPackageReceipt WithToolTranscriptCharacters(int characterCount) => this with
    {
        ToolTranscriptCharacters = characterCount < 0
            ? throw new ArgumentOutOfRangeException(nameof(characterCount))
            : characterCount
    };

    public WorkerContextPackageReceipt WithValidatedContext() => this with
    {
        ValidatedForIdempotentReuse = true
    };

    public WorkerContextPackageReceipt WithBaselineMeasurements(
        int promptCharacters,
        int deliveredArtifactBytes,
        int toolTranscriptCharacters) => this with
    {
        BaselinePromptCharacters = RequireNonNegative(promptCharacters, nameof(promptCharacters)),
        BaselineDeliveredArtifactBytes = RequireNonNegative(deliveredArtifactBytes, nameof(deliveredArtifactBytes)),
        BaselineToolTranscriptCharacters = RequireNonNegative(toolTranscriptCharacters, nameof(toolTranscriptCharacters)),
        BaselineModelInputTokenEstimate = EstimateInputTokens(RequireNonNegative(promptCharacters, nameof(promptCharacters)))
    };

    private static int RequireNonNegative(int value, string parameterName) => value < 0
        ? throw new ArgumentOutOfRangeException(parameterName)
        : value;

    private static int EstimateInputTokens(int characterCount) =>
        characterCount / 4 + (characterCount % 4 == 0 ? 0 : 1);

    private static ProviderUsageValue MergeUsageValue(
        ProviderUsageValue current,
        long? reportedValue,
        string unavailableReason)
    {
        if (current.State == ProviderUsageState.Reported)
        {
            return current;
        }

        return reportedValue is { } reported
            ? ProviderUsageValue.Reported(reported)
            : ProviderUsageValue.Unknown(unavailableReason);
    }
}

public sealed record ProviderReportedUsage(
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens = null);

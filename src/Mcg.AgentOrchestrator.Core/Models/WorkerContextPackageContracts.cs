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
    MandatoryFile
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
        string? fallbackReason)
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
    public static WorkerContextArtifact Create(
        LogicalArtifactIdentity identity,
        ContextArtifactKind kind,
        byte[]? authoritativeBytes,
        IEnumerable<AgentRole> roleVisibility,
        ContextDeliveryMode deliveryMode,
        ContextContractVersion contractVersion,
        string? mandatoryRelativePath = null,
        string? expectedContentHash = null,
        string? fallbackReason = null)
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

        if (deliveryMode == ContextDeliveryMode.InlineFull && authoritativeBytes is null)
        {
            throw new ArgumentException("InlineFull requires complete authoritative bytes.", nameof(authoritativeBytes));
        }

        if (deliveryMode == ContextDeliveryMode.MandatoryFile && string.IsNullOrWhiteSpace(mandatoryRelativePath))
        {
            throw new ArgumentException("MandatoryFile requires a relative materialization path.", nameof(mandatoryRelativePath));
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
            fallbackReason);
    }

    public WorkerContextArtifact WithInlineFallback(string reason)
    {
        if (AuthoritativeBytes is null)
        {
            throw new WorkerContextPreparationException(Identity, reason, "Complete authoritative bytes are unavailable for inline fallback.");
        }

        return Create(
            Identity,
            Kind,
            AuthoritativeBytes,
            RoleVisibility,
            ContextDeliveryMode.InlineFull,
            ContractVersion,
            expectedContentHash: ContentHash,
            fallbackReason: reason);
    }

    public static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed record WorkerContextPackage(
    string SemanticPackageId,
    ContextContractVersion ContractVersion,
    AgentRole TargetRole,
    IReadOnlyList<WorkerContextArtifact> Artifacts)
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
    string? MandatoryRelativePath = null);

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
    ProviderUsageValue OutputTokens)
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

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerContextPackageBuilder
{
    private const string PackageIdDomain = "mcg-worker-context-package";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ContextDeliveryMode SelectDeliveryMode(ContextArtifactKind kind) => kind switch
    {
        ContextArtifactKind.ResearcherEvidence or
        ContextArtifactKind.PlannerPlan or
        ContextArtifactKind.PriorTaskEvidence or
        ContextArtifactKind.RegisteredContext => ContextDeliveryMode.MandatoryFile,
        _ => ContextDeliveryMode.InlineFull
    };

    internal static T[] DistinctBySerializedValue<T>(IEnumerable<T> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        return snapshots
            .DistinctBy(
                snapshot => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(snapshot)),
                StringComparer.Ordinal)
            .ToArray();
    }

    public WorkerContextPackage Prepare(
        AgentRole targetRole,
        string contextRoot,
        IEnumerable<WorkerContextArtifact> artifacts,
        bool allowInlineFallback = true)
    {
        if (targetRole is not (AgentRole.Researcher or AgentRole.Planner or AgentRole.Developer or AgentRole.Tester or AgentRole.Reviewer))
        {
            throw new ArgumentOutOfRangeException(nameof(targetRole), "Typed worker context packages are limited to the five SDLC roles in contract v1.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(contextRoot);
        var root = Path.GetFullPath(contextRoot);
        var effective = new List<WorkerContextArtifact>();
        foreach (var artifact in Deduplicate(artifacts))
        {
            if (!artifact.RoleVisibility.Contains(targetRole))
            {
                throw new WorkerContextPreparationException(
                    artifact.Identity,
                    "visibility-incompatible",
                    $"Target role {targetRole} is not in the artifact visibility allow-list.");
            }

            if (artifact.ContractVersion != ContextContractVersion.V1)
            {
                throw new WorkerContextPreparationException(
                    artifact.Identity,
                    "version-incompatible",
                    $"Contract version {artifact.ContractVersion.Value} is not supported by package v1.");
            }

            if (artifact.DeliveryMode == ContextDeliveryMode.InlineFull)
            {
                ValidateAuthoritativeBytes(artifact);
                effective.Add(artifact);
                continue;
            }

            var failure = ValidateMandatoryFile(root, artifact);
            effective.Add(failure is null
                ? artifact
                : RecoverMandatoryFileOrThrow(root, artifact, failure, allowInlineFallback));
        }

        var ordered = effective
            .OrderBy(SerializeIdentityTuple, ByteArrayComparer.Instance)
            .ToArray();
        return new WorkerContextPackage(
            ComputeSemanticPackageId(ordered),
            ContextContractVersion.V1,
            targetRole,
            ordered);
    }

    public WorkerContextPackage AppendFinalizedInlineArtifact(
        WorkerContextPackage prepared,
        WorkerContextArtifact inlineArtifact)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(inlineArtifact);
        if (prepared.ContractVersion != ContextContractVersion.V1 ||
            inlineArtifact.ContractVersion != prepared.ContractVersion)
        {
            throw new WorkerContextPreparationException(
                inlineArtifact.Identity,
                "version-incompatible",
                "The appended artifact must use the finalized package contract version.");
        }

        if (inlineArtifact.DeliveryMode != ContextDeliveryMode.InlineFull ||
            !inlineArtifact.RoleVisibility.Contains(prepared.TargetRole))
        {
            throw new WorkerContextPreparationException(
                inlineArtifact.Identity,
                "finalized-append-invalid",
                "Only a visible InlineFull artifact may be appended to a finalized package.");
        }

        ValidateAuthoritativeBytes(inlineArtifact);
        var ordered = Deduplicate(prepared.Artifacts.Append(inlineArtifact))
            .OrderBy(SerializeIdentityTuple, ByteArrayComparer.Instance)
            .ToArray();
        return new WorkerContextPackage(
            ComputeSemanticPackageId(ordered),
            prepared.ContractVersion,
            prepared.TargetRole,
            ordered,
            prepared.ReviewFindingProjection);
    }

    public static string Render(WorkerContextPackage package)
    {
        var lines = new List<string>
        {
            "## Worker Context Package",
            $"Contract: {package.ContractVersion.Value}",
            $"Semantic package identity (attestation/cache only; not delivery proof): {package.SemanticPackageId}"
        };

        foreach (var artifact in package.Artifacts)
        {
            lines.Add(string.Empty);
            lines.Add(RenderArtifact(artifact));
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static byte[] RecoverInlineBytes(string encodedPayload, string encoding) => encoding switch
    {
        "utf8-json" => StrictUtf8.GetBytes(JsonSerializer.Deserialize<string>(encodedPayload)
            ?? throw new FormatException("Inline UTF-8 JSON payload was null.")),
        "base64" => Convert.FromBase64String(encodedPayload),
        _ => throw new FormatException($"Unsupported inline encoding '{encoding}'.")
    };

    public WorkerContextPackage RehydrateAndAppendInlineArtifact(
        AgentRole targetRole,
        string contextRoot,
        string renderedPackage,
        WorkerContextPackageReceipt receipt,
        LogicalArtifactIdentity identity,
        byte[] authoritativeBytes)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(renderedPackage);
        ArgumentNullException.ThrowIfNull(authoritativeBytes);
        if (!renderedPackage.Contains(receipt.SemanticPackageId, StringComparison.Ordinal))
        {
            throw new WorkerContextPreparationException(
                identity,
                "receipt-package-mismatch",
                "The persisted receipt semantic identity is absent from the rendered package.");
        }

        var artifacts = receipt.Sections
            .Where(section => !section.LogicalIdentity.Equals("context/manifest.v1.json", StringComparison.Ordinal))
            .Select(section => RehydrateArtifact(renderedPackage, section))
            .Append(WorkerContextArtifact.Create(
                identity,
                ContextArtifactKind.OperatorInstructions,
                authoritativeBytes,
                [targetRole],
                ContextDeliveryMode.InlineFull,
                ContextContractVersion.V1))
            .ToArray();
        var prepared = Prepare(targetRole, contextRoot, artifacts, allowInlineFallback: false);
        return WorkerProfileDispatcher.FinalizeContextPackageWithManifest(this, prepared);
    }

    public static WorkerContextPackageReceipt CreateReceipt(WorkerContextPackage package)
    {
        var sections = package.Artifacts.Select(artifact =>
        {
            var rendered = RenderArtifact(artifact);
            return new WorkerContextSectionReceipt(
                artifact.Identity.Value,
                rendered.Length,
                artifact.AuthoritativeByteCount,
                artifact.ContentHash,
                artifact.DeliveryMode,
                artifact.ContractVersion.Value,
                artifact.RoleVisibility,
                artifact.FallbackReason,
                artifact.MandatoryRelativePath,
                artifact.Kind);
        }).ToArray();

        var projection = package.ReviewFindingProjection;
        return new WorkerContextPackageReceipt(
            package.SemanticPackageId,
            sections,
            ProviderUsageValue.Unknown("not-yet-reported"),
            ProviderUsageValue.Unknown("not-yet-reported"),
            ProviderUsageValue.Unknown("not-yet-reported"),
            Encoding.UTF8.GetByteCount(Render(package)),
            projection?.Mode,
            projection?.UniqueRoundCount ?? 0,
            projection?.DuplicateRoundCount ?? 0,
            projection?.UniqueReceiptCount ?? 0,
            projection?.DuplicateReceiptCount ?? 0,
            projection?.FallbackReason);
    }

    internal static string RenderArtifact(WorkerContextArtifact artifact)
    {
        if (artifact.DeliveryMode == ContextDeliveryMode.MandatoryFile)
        {
            var validation = artifact.FallbackReason is null ? "verified" : "recovered";
            var problemExcerpt = artifact.FallbackReason is null
                ? string.Empty
                : $"; problem_excerpt={BoundProblemExcerpt(artifact.FallbackReason)}";
            return $"MANDATORY READ: identity={artifact.Identity.Value}; purpose={artifact.Kind}; path={artifact.MandatoryRelativePath}; bytes={artifact.AuthoritativeByteCount}; sha256={artifact.ContentHash}; contract={artifact.ContractVersion.Value}; validation={validation}{problemExcerpt}. " +
                "The complete artifact remains readable at the path on demand. Normally report only this identity receipt and exact source locations; do not reproduce the artifact body on the happy path.";
        }

        var bytes = artifact.AuthoritativeBytes!;
        var encoding = TryDecodeUtf8(bytes, out var text) ? "utf8-json" : "base64";
        var payload = encoding == "utf8-json" ? JsonSerializer.Serialize(text) : Convert.ToBase64String(bytes);
        return $"INLINE FULL: identity={artifact.Identity.Value}; sha256={artifact.ContentHash}; contract={artifact.ContractVersion.Value}; encoding={encoding}" +
            Environment.NewLine + payload;
    }

    private static WorkerContextArtifact RehydrateArtifact(
        string renderedPackage,
        WorkerContextSectionReceipt section)
    {
        var identity = new LogicalArtifactIdentity(section.LogicalIdentity);
        var version = new ContextContractVersion(section.ContractVersion);
        if (section.DeliveryMode == ContextDeliveryMode.MandatoryFile)
        {
            return WorkerContextArtifact.Create(
                identity,
                section.ArtifactKind ?? ContextArtifactKind.RegisteredContext,
                authoritativeBytes: null,
                section.RoleVisibility,
                section.DeliveryMode,
                version,
                section.MandatoryRelativePath,
                section.ContentHash,
                section.FallbackReason,
                section.ByteCount);
        }

        var headerPrefix = $"INLINE FULL: identity={section.LogicalIdentity}; sha256={section.ContentHash}; contract={section.ContractVersion}; encoding=";
        var normalized = renderedPackage.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var headerIndexes = lines
            .Select((line, index) => (line, index))
            .Where(item => item.line.StartsWith(headerPrefix, StringComparison.Ordinal))
            .ToArray();
        if (headerIndexes.Length != 1 || headerIndexes[0].index + 1 >= lines.Length)
        {
            throw new WorkerContextPreparationException(
                identity,
                "inline-rehydration-ambiguous",
                $"Expected exactly one rendered inline payload, found {headerIndexes.Length}.");
        }

        var header = headerIndexes[0].line;
        var encoding = header[headerPrefix.Length..];
        var bytes = RecoverInlineBytes(lines[headerIndexes[0].index + 1], encoding);
        return WorkerContextArtifact.Create(
            identity,
            ContextArtifactKind.RegisteredContext,
            bytes,
            section.RoleVisibility,
            section.DeliveryMode,
            version,
            expectedContentHash: section.ContentHash,
            fallbackReason: section.FallbackReason);
    }

    private static IEnumerable<WorkerContextArtifact> Deduplicate(IEnumerable<WorkerContextArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var byIdentity = new Dictionary<string, WorkerContextArtifact>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            if (!byIdentity.TryGetValue(artifact.Identity.Value, out var existing))
            {
                byIdentity.Add(artifact.Identity.Value, artifact);
                continue;
            }

            if (!SerializeIdentityTuple(existing).AsSpan().SequenceEqual(SerializeIdentityTuple(artifact)) ||
                !existing.ContentHash.Equals(artifact.ContentHash, StringComparison.Ordinal) ||
                !string.Equals(existing.MandatoryRelativePath, artifact.MandatoryRelativePath, StringComparison.Ordinal))
            {
                throw new WorkerContextPreparationException(
                    artifact.Identity,
                    "duplicate-identity-conflict",
                    "The same logical identity has conflicting package metadata.");
            }
        }

        return byIdentity.Values;
    }

    private static WorkerContextArtifact RecoverMandatoryFileOrThrow(
        string root,
        WorkerContextArtifact artifact,
        string reason,
        bool allowInlineFallback)
    {
        if (allowInlineFallback && artifact.AuthoritativeBytes is not null)
        {
            ValidateAuthoritativeBytes(artifact);
            var recoveryRelativePath = $".orchestrator-context/recovered/{artifact.ContentHash}.bin";
            var recoveryDirectory = Path.Combine(root, ".orchestrator-context", "recovered");
            try
            {
                if (Directory.Exists(recoveryDirectory) &&
                    (File.GetAttributes(recoveryDirectory) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new WorkerContextPreparationException(
                        artifact.Identity,
                        "recovery-reparse-point",
                        "The package-owned recovery directory is a reparse point.");
                }

                Directory.CreateDirectory(recoveryDirectory);
                var recoveryPath = Path.Combine(recoveryDirectory, $"{artifact.ContentHash}.bin");
                if (File.Exists(recoveryPath) &&
                    (File.GetAttributes(recoveryPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new WorkerContextPreparationException(
                        artifact.Identity,
                        "recovery-reparse-point",
                        "The package-owned recovery file is a reparse point.");
                }

                File.WriteAllBytes(recoveryPath, artifact.AuthoritativeBytes);
                var recovered = WorkerContextArtifact.Create(
                    artifact.Identity,
                    artifact.Kind,
                    artifact.AuthoritativeBytes,
                    artifact.RoleVisibility,
                    ContextDeliveryMode.MandatoryFile,
                    artifact.ContractVersion,
                    recoveryRelativePath,
                    artifact.ContentHash,
                    reason);
                var recoveryFailure = ValidateMandatoryFile(root, recovered);
                if (recoveryFailure is not null)
                {
                    throw new WorkerContextPreparationException(
                        artifact.Identity,
                        $"recovery-{recoveryFailure}",
                        "Recovered mandatory artifact did not pass package validation.");
                }

                return recovered;
            }
            catch (WorkerContextPreparationException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new WorkerContextPreparationException(
                    artifact.Identity,
                    "recovery-materialization-failed",
                    $"Package-owned recovery materialization failed: {exception.GetType().Name}.");
            }
        }

        throw new WorkerContextPreparationException(
            artifact.Identity,
            reason,
            "Mandatory artifact cannot be supplied through a verified delivery channel.");
    }

    private static string BoundProblemExcerpt(string value) =>
        value.Length <= 240 ? value : value[..240] + $"...[{value.Length - 240} chars omitted]";

    private static void ValidateAuthoritativeBytes(WorkerContextArtifact artifact)
    {
        if (artifact.AuthoritativeBytes is null ||
            !WorkerContextArtifact.Hash(artifact.AuthoritativeBytes).Equals(artifact.ContentHash, StringComparison.Ordinal))
        {
            throw new WorkerContextPreparationException(
                artifact.Identity,
                "authoritative-hash-mismatch",
                "Complete authoritative bytes are missing or do not match the declared hash.");
        }
    }

    private static string? ValidateMandatoryFile(string root, WorkerContextArtifact artifact)
    {
        string relative;
        try
        {
            relative = new LogicalArtifactIdentity(artifact.MandatoryRelativePath!).Value;
        }
        catch (ArgumentException)
        {
            return "invalid-path";
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var relativeToRoot = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relativeToRoot) || relativeToRoot == ".." ||
            relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return "path-outside-context-root";
        }

        if (!File.Exists(fullPath))
        {
            return "missing";
        }

        try
        {
            for (FileSystemInfo? current = new FileInfo(fullPath); current is not null; current = current switch
                {
                    FileInfo file => file.Directory,
                    DirectoryInfo directory => directory.Parent,
                    _ => null
                })
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return "reparse-point";
                }

                if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            return hash.Equals(artifact.ContentHash, StringComparison.Ordinal) ? null : "hash-mismatch";
        }
        catch (UnauthorizedAccessException)
        {
            return "unreadable";
        }
        catch (IOException)
        {
            return "unreadable";
        }
    }

    internal static string ComputeSemanticPackageId(IEnumerable<WorkerContextArtifact> artifacts)
    {
        using var stream = new MemoryStream();
        WriteField(stream, PackageIdDomain);
        WriteField(stream, "1");
        foreach (var tuple in artifacts.Select(SerializeIdentityTuple).OrderBy(value => value, ByteArrayComparer.Instance))
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, tuple.Length);
            stream.Write(length);
            stream.Write(tuple);
        }

        return $"ctxpkg-v1-sha256:{WorkerContextArtifact.Hash(stream.ToArray())}";
    }

    private static byte[] SerializeIdentityTuple(WorkerContextArtifact artifact)
    {
        using var stream = new MemoryStream();
        WriteField(stream, artifact.Identity.Value);
        WriteField(stream, artifact.ContentHash);
        WriteField(stream, string.Join(',', artifact.RoleVisibility.Select(role => role.ToString())));
        WriteField(stream, artifact.DeliveryMode.ToString());
        WriteField(stream, artifact.ContractVersion.ToString());
        return stream.ToArray();
    }

    private static void WriteField(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private static bool TryDecodeUtf8(byte[] bytes, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(bytes);
            return StrictUtf8.GetBytes(text).AsSpan().SequenceEqual(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();

        public int Compare(byte[]? left, byte[]? right) => left.AsSpan().SequenceCompareTo(right);
    }
}

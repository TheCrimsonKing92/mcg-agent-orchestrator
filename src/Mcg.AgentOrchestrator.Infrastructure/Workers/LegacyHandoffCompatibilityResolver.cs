using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record LegacyHandoffPointer(
    int Version,
    string AuthoritativeEvidenceIdentity,
    string Sha256,
    string? MaterializationPath = null,
    string? EmbeddedPayload = null);

public sealed record ResolvedLegacyHandoffArtifact(
    int Version,
    string LogicalIdentity,
    byte[] Bytes);

public sealed class LegacyHandoffCompatibilityResolver
{
    private const string PointerPrefix = "Compatibility pointer (v1, hash-bound; resolve from authoritative task evidence): ";
    private readonly Func<string, byte[]?> _loadAuthoritativeBytes;
    private readonly string? _materializationRoot;

    public LegacyHandoffCompatibilityResolver(
        Func<string, byte[]?> loadAuthoritativeBytes,
        string? materializationRoot = null)
    {
        _loadAuthoritativeBytes = loadAuthoritativeBytes ?? throw new ArgumentNullException(nameof(loadAuthoritativeBytes));
        _materializationRoot = materializationRoot is null ? null : Path.GetFullPath(materializationRoot);
    }

    public byte[] Resolve(string compatibilityRepresentation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compatibilityRepresentation);
        LegacyHandoffPointer pointer;
        try
        {
            pointer = JsonSerializer.Deserialize<LegacyHandoffPointer>(compatibilityRepresentation)
                ?? throw new FormatException("Legacy handoff representation was empty.");
        }
        catch (JsonException error)
        {
            throw new FormatException("Legacy handoff representation is not valid JSON.", error);
        }

        byte[] bytes = pointer.Version switch
        {
            0 when pointer.EmbeddedPayload is not null => Convert.FromBase64String(pointer.EmbeddedPayload),
            1 => ResolveV1(pointer),
            _ => throw new NotSupportedException($"Legacy handoff version {pointer.Version} is not supported.")
        };

        var actualHash = WorkerContextArtifact.Hash(bytes);
        if (!actualHash.Equals(pointer.Sha256, StringComparison.Ordinal))
        {
            throw new WorkerContextPreparationException(
                new LogicalArtifactIdentity(pointer.AuthoritativeEvidenceIdentity),
                "authoritative-evidence-hash-mismatch",
                $"Expected {pointer.Sha256}, found {actualHash}.");
        }

        return bytes;
    }

    private byte[] ResolveV1(LegacyHandoffPointer pointer)
    {
        var retained = _loadAuthoritativeBytes(pointer.AuthoritativeEvidenceIdentity);
        if (retained is not null)
        {
            return retained;
        }

        if (_materializationRoot is null || string.IsNullOrWhiteSpace(pointer.MaterializationPath))
        {
            throw MissingEvidence(pointer.AuthoritativeEvidenceIdentity);
        }

        string relativePath;
        try
        {
            relativePath = new LogicalArtifactIdentity(pointer.MaterializationPath).Value;
        }
        catch (ArgumentException)
        {
            throw MissingEvidence(pointer.AuthoritativeEvidenceIdentity);
        }

        var fullPath = Path.GetFullPath(Path.Combine(
            _materializationRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relativeToRoot = Path.GetRelativePath(_materializationRoot, fullPath);
        if (Path.IsPathRooted(relativeToRoot) || relativeToRoot == ".." ||
            relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !File.Exists(fullPath))
        {
            throw MissingEvidence(pointer.AuthoritativeEvidenceIdentity);
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
                    throw MissingEvidence(pointer.AuthoritativeEvidenceIdentity);
                }

                if (string.Equals(
                    current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    _materializationRoot.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            return File.ReadAllBytes(fullPath);
        }
        catch (UnauthorizedAccessException)
        {
            throw MissingEvidence(pointer.AuthoritativeEvidenceIdentity);
        }
        catch (IOException)
        {
            throw MissingEvidence(pointer.AuthoritativeEvidenceIdentity);
        }
    }

    private static WorkerContextPreparationException MissingEvidence(string identity) => new(
        new LogicalArtifactIdentity(identity),
        "authoritative-evidence-missing",
        "The v1 compatibility pointer did not resolve to retained or materialized evidence.");

    public IReadOnlyList<byte[]> ResolveAllFromMarkdown(string handoffMarkdown)
        => ResolveArtifactsFromMarkdown(handoffMarkdown).Select(artifact => artifact.Bytes).ToArray();

    public IReadOnlyList<ResolvedLegacyHandoffArtifact> ResolveArtifactsFromMarkdown(string handoffMarkdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handoffMarkdown);
        var resolved = new List<ResolvedLegacyHandoffArtifact>();
        using var reader = new StringReader(handoffMarkdown);
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith(PointerPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var representation = line[PointerPrefix.Length..];
            var pointer = JsonSerializer.Deserialize<LegacyHandoffPointer>(representation)
                ?? throw new FormatException("Legacy handoff representation was empty.");
            resolved.Add(new ResolvedLegacyHandoffArtifact(
                pointer.Version,
                pointer.AuthoritativeEvidenceIdentity,
                Resolve(representation)));
        }

        if (resolved.Count > 0)
        {
            return resolved;
        }

        var embeddedMatches = Regex.Matches(
            handoffMarkdown,
            @"^### Verification Output\r?\n(?<payload>.*?)(?:\r?\n\r?\n---(?:\r?\n|$))",
            RegexOptions.Multiline | RegexOptions.Singleline);
        for (var index = 0; index < embeddedMatches.Count; index++)
        {
            resolved.Add(new ResolvedLegacyHandoffArtifact(
                0,
                $"legacy-handoff/v0/{index + 1}",
                Encoding.UTF8.GetBytes(embeddedMatches[index].Groups["payload"].Value)));
        }

        return resolved.Count > 0
            ? resolved
            : throw new FormatException("Legacy handoff did not contain a supported v1 pointer or v0 embedded verification section.");
    }

    public static string CreateV1Pointer(
        LogicalArtifactIdentity identity,
        ReadOnlySpan<byte> authoritativeBytes,
        string? materializationPath = null) =>
        JsonSerializer.Serialize(new LegacyHandoffPointer(
            1,
            identity.Value,
            WorkerContextArtifact.Hash(authoritativeBytes),
            materializationPath));
}

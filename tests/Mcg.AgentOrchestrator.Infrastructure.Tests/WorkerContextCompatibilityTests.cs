using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerContextCompatibilityTests
{
    [Xunit.Fact]
    public void ResolveAllFromMarkdown_V1Pointers_RecoversCompleteEvidence()
    {
        var first = Encoding.UTF8.GetBytes("first evidence\n");
        var second = Encoding.UTF8.GetBytes("second café\r\n");
        var firstIdentity = new LogicalArtifactIdentity("prior/first/verification-output");
        var secondIdentity = new LogicalArtifactIdentity("prior/second/verification-output");
        var markdown = string.Join(Environment.NewLine,
            "# Prior Task Handoff",
            "Compatibility pointer (v1, hash-bound; resolve from authoritative task evidence): " +
                LegacyHandoffCompatibilityResolver.CreateV1Pointer(firstIdentity, first),
            "Compatibility pointer (v1, hash-bound; resolve from authoritative task evidence): " +
                LegacyHandoffCompatibilityResolver.CreateV1Pointer(secondIdentity, second));
        var evidence = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [firstIdentity.Value] = first,
            [secondIdentity.Value] = second
        };

        var resolved = new LegacyHandoffCompatibilityResolver(identity => evidence.GetValueOrDefault(identity))
            .ResolveAllFromMarkdown(markdown);

        Assert.Equal(2, resolved.Count);
        Assert.Equal(first, resolved[0]);
        Assert.Equal(second, resolved[1]);
    }

    [Xunit.Fact]
    public void ResolveAllFromMarkdown_MissingEvidence_FailsClosed()
    {
        var bytes = Encoding.UTF8.GetBytes("retained");
        var pointer = LegacyHandoffCompatibilityResolver.CreateV1Pointer(
            new LogicalArtifactIdentity("prior/missing/verification-output"), bytes);
        var markdown = "Compatibility pointer (v1, hash-bound; resolve from authoritative task evidence): " + pointer;

        var error = Assert.Throws<WorkerContextPreparationException>(() =>
            new LegacyHandoffCompatibilityResolver(_ => null).ResolveAllFromMarkdown(markdown));

        Assert.Equal("authoritative-evidence-missing", error.Reason);
    }

    [Xunit.Fact]
    public void ResolveAllFromMarkdown_V1Materialization_RecoversWithoutDatabaseLoader()
    {
        var root = CreateTempDirectory();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("complete materialized evidence\n");
            var identity = new LogicalArtifactIdentity("prior/materialized/verification-output");
            const string relativePath = ".orchestrator-context/legacy-handoff/materialized/verification-output.bin";
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, bytes);
            var pointer = LegacyHandoffCompatibilityResolver.CreateV1Pointer(identity, bytes, relativePath);
            var markdown = "Compatibility pointer (v1, hash-bound; resolve from authoritative task evidence): " + pointer;

            var resolved = new LegacyHandoffCompatibilityResolver(_ => null, root)
                .ResolveAllFromMarkdown(markdown);

            Assert.Equal(bytes, Assert.Single(resolved));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ResolveAllFromMarkdown_CorruptV1Materialization_FailsClosed()
    {
        var root = CreateTempDirectory();
        try
        {
            var authoritative = Encoding.UTF8.GetBytes("authoritative");
            var identity = new LogicalArtifactIdentity("prior/materialized/verification-output");
            const string relativePath = ".orchestrator-context/legacy-handoff/materialized/verification-output.bin";
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "corrupt");
            var pointer = LegacyHandoffCompatibilityResolver.CreateV1Pointer(identity, authoritative, relativePath);

            var error = Assert.Throws<WorkerContextPreparationException>(() =>
                new LegacyHandoffCompatibilityResolver(_ => null, root).Resolve(pointer));

            Assert.Equal("authoritative-evidence-hash-mismatch", error.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Resolve_MissingFile_ReportsFileNotFoundAndPath()
    {
        var root = CreateTempDirectory();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("missing materialization");
            var identity = new LogicalArtifactIdentity("prior/missing-file/verification-output");
            const string relativePath = "evidence/missing.bin";
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var pointer = LegacyHandoffCompatibilityResolver.CreateV1Pointer(identity, bytes, relativePath);

            var error = Assert.Throws<WorkerContextPreparationException>(() =>
                new LegacyHandoffCompatibilityResolver(_ => null, root).Resolve(pointer));

            Assert.Equal("authoritative-evidence-missing", error.Reason);
            Assert.Contains(nameof(FileNotFoundException), error.Message, StringComparison.Ordinal);
            Assert.Contains(fullPath, error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Resolve_ReadIOException_ReportsUnreadableAndPath()
    {
        var root = CreateTempDirectory();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("locked materialization");
            var identity = new LogicalArtifactIdentity("prior/locked/verification-output");
            const string relativePath = "evidence/locked.bin";
            var fullPath = WriteMaterialization(root, relativePath, bytes);
            Assert.True(File.Exists(fullPath));
            var pointer = LegacyHandoffCompatibilityResolver.CreateV1Pointer(identity, bytes, relativePath);

            var error = Assert.Throws<WorkerContextPreparationException>(() =>
                new LegacyHandoffCompatibilityResolver(
                    _ => null,
                    root,
                    null,
                    _ => throw new IOException("fixture lock"))
                .Resolve(pointer));

            Assert.Equal("authoritative-evidence-unreadable", error.Reason);
            Assert.Contains(nameof(IOException), error.Message, StringComparison.Ordinal);
            Assert.Contains(fullPath, error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Resolve_AccessDenied_ReportsPermissionAndPath()
    {
        var root = CreateTempDirectory();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("permission materialization");
            var identity = new LogicalArtifactIdentity("prior/permission/verification-output");
            const string relativePath = "evidence/permission.bin";
            var fullPath = WriteMaterialization(root, relativePath, bytes);
            Assert.True(File.Exists(fullPath));
            var pointer = LegacyHandoffCompatibilityResolver.CreateV1Pointer(identity, bytes, relativePath);

            var error = Assert.Throws<WorkerContextPreparationException>(() =>
                new LegacyHandoffCompatibilityResolver(
                    _ => null,
                    root,
                    null,
                    _ => throw new UnauthorizedAccessException("fixture ACL"))
                .Resolve(pointer));

            Assert.Equal("authoritative-evidence-permission-denied", error.Reason);
            Assert.Contains(nameof(UnauthorizedAccessException), error.Message, StringComparison.Ordinal);
            Assert.Contains(fullPath, error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Resolve_DisallowedFallbackIdentity_FailsClosed()
    {
        var root = CreateTempDirectory();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("unrelated materialization");
            var identity = new LogicalArtifactIdentity("prior/unrelated/verification-output");
            const string relativePath = "evidence/unrelated.bin";
            WriteMaterialization(root, relativePath, bytes);
            var pointer = LegacyHandoffCompatibilityResolver.CreateV1Pointer(identity, bytes, relativePath);

            var error = Assert.Throws<WorkerContextPreparationException>(() =>
                new LegacyHandoffCompatibilityResolver(
                    _ => null,
                    root,
                    "prior/current/verification-output",
                    File.ReadAllBytes)
                .Resolve(pointer));

            Assert.Equal("authoritative-evidence-missing", error.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ResolveArtifactsFromMarkdown_V0EmbeddedSchema_RecoversPayload()
    {
        var markdown = string.Join("\r\n",
            "# Prior Task Handoff",
            "",
            "## Planner: prior plan",
            "",
            "### Verification Output",
            "first line",
            "second café",
            "",
            "---",
            "");

        var artifact = Assert.Single(
            new LegacyHandoffCompatibilityResolver(_ => null).ResolveArtifactsFromMarkdown(markdown));

        Assert.Equal(0, artifact.Version);
        Assert.Equal("legacy-handoff/v0/1", artifact.LogicalIdentity);
        Assert.Equal(Encoding.UTF8.GetBytes("first line\r\nsecond café"), artifact.Bytes);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-context-compatibility-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteMaterialization(string root, string relativePath, byte[] bytes)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, bytes);
        return fullPath;
    }
}

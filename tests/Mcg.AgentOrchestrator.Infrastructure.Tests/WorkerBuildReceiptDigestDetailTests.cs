using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each receipt and project layout has a unique temporary root; git and builds are faked.
public sealed class WorkerBuildReceiptDigestDetailTests
{
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Digest = "wbc1:git-tree:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnavailableDigestDetailReachesBuildDiagnostic(bool buildRan)
    {
        var root = CreateRoot();
        try
        {
            var path = WriteReceipt(root, Digest, []);
            var calls = new List<string>();
            var verdict = WorkerBuildReceipt.Evaluate(path, root, (runnerRoot, _, args) =>
            {
                Assert.Equal(root, runnerRoot);
                calls.Add(args[0]);
                return args[0] == "add"
                    ? new(128, "", "fatal: unable to create index.lock\nmore stderr", false)
                    : Succeeded(args);
            });

            Assert.Equal(["rev-parse", "read-tree", "add"], calls);
            Assert.False(verdict.Matches);
            Assert.Equal("digest-unavailable", verdict.Reason);
            Assert.Equal("step=add exit=128 stderr=fatal: unable to create index.lock", verdict.Detail);
            var resolution = Resolve(root, verdict, buildRan);
            Assert.Contains("worker_build_receipt=digest-unavailable; digest_detail=" + verdict.Detail,
                resolution.Diagnostic);
            Assert.Contains(buildRan ? "build_evidence_producer=orchestrator" : "build_evidence_attempt=orchestrator",
                resolution.Diagnostic);
            Assert.Equal(!buildRan, resolution.MissingEvidence);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MatchingDigestHasNoDetailAndSkipsCoveredBuild()
    {
        var root = CreateRoot();
        try
        {
            var path = WriteReceipt(root, Digest, ["App/App.csproj"]);
            var calls = new List<string>();
            var verdict = WorkerBuildReceipt.Evaluate(path, root, (_, _, args) =>
            {
                calls.Add(args[0]);
                return Succeeded(args);
            });
            Assert.Equal(["rev-parse", "read-tree", "add", "write-tree"], calls);
            Assert.True(verdict.Matches);
            Assert.Equal("matched", verdict.Reason);
            Assert.Null(verdict.Detail);
            var resolution = OrchestratorBuildEvidenceCheck.Resolve(root, AgentRole.Developer,
                ["App/Changed.cs"], [], false, () => verdict,
                _ => throw new InvalidOperationException("Covered receipt must not trigger a build."));
            Assert.False(resolution.FailsRound);
            Assert.Equal("", resolution.Diagnostic);
            Assert.DoesNotContain("digest_detail=", resolution.Diagnostic);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MatchingDigestWithIncompleteCoverageHasNoDetailInDiagnostic()
    {
        var root = CreateRoot();
        try
        {
            var verdict = WorkerBuildReceipt.Evaluate(WriteReceipt(root, Digest, []), root,
                (_, _, args) => Succeeded(args));
            Assert.True(verdict.Matches);
            Assert.Equal("matched", verdict.Reason);
            Assert.Null(verdict.Detail);
            var resolution = Resolve(root, verdict, buildRan: true);
            Assert.Contains("worker_build_receipt=projects-incomplete; missing_projects=App/App.csproj;",
                resolution.Diagnostic);
            Assert.DoesNotContain("digest_detail=", resolution.Diagnostic);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DigestMismatchRemainsMismatchWithoutDetailAndRetainsBuildFailure()
    {
        var root = CreateRoot();
        try
        {
            var verdict = WorkerBuildReceipt.Evaluate(
                WriteReceipt(root, "wbc1:git-tree:" + new string('b', 40), ["App/App.csproj"]), root,
                (_, _, args) => Succeeded(args));
            Assert.False(verdict.Matches);
            Assert.Equal("digest-mismatch", verdict.Reason);
            Assert.Null(verdict.Detail);
            var resolution = OrchestratorBuildEvidenceCheck.Resolve(root, AgentRole.Developer,
                ["App/Changed.cs"], [], false, () => verdict, _ => new(true, 1, "FAIL build: assertion failure"));
            Assert.True(resolution.BuildFailed);
            Assert.True(resolution.FailsRound);
            Assert.Contains("worker_build_receipt=digest-mismatch; build_evidence_producer=orchestrator;",
                resolution.Diagnostic);
            Assert.DoesNotContain("digest_detail=", resolution.Diagnostic);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static OrchestratorBuildEvidenceResolution Resolve(string root, WorkerBuildReceiptVerdict verdict, bool buildRan)
    {
        var builds = 0;
        OrchestratorBuildCheckRequest? buildRequest = null;
        var resolution = OrchestratorBuildEvidenceCheck.Resolve(root, AgentRole.Developer,
            ["App/Changed.cs"], [], false, () => verdict, request =>
            {
                builds++;
                buildRequest = request;
                return new(buildRan, buildRan ? 0 : -1, buildRan ? "PASS build: 0 errors" : "build unavailable");
            });
        Assert.Equal(1, builds);
        Assert.NotNull(buildRequest);
        Assert.Equal(root, buildRequest.WorktreeRoot);
        Assert.Equal(["App/App.csproj"], buildRequest.Projects);
        return resolution;
    }

    private static WorktreeTreeDigestGitStep Succeeded(string[] args) =>
        new(0, args[0] == "rev-parse" ? "fake-objects" : Hash, null, false);

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "receipt-digest-detail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "App"));
        File.WriteAllText(Path.Combine(root, "App", "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "App", "Changed.cs"), "public class Changed { }");
        return root;
    }

    private static string WriteReceipt(string root, string digest, string[] projects)
    {
        var path = Path.Combine(root, WorkerBuildReceipt.FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            treeDigest = digest,
            digestAlgorithm = WorktreeTreeDigest.Algorithm,
            buildOutcome = "success",
            generatedAtUtc = "2026-10-11T12:00:00Z",
            worktreeRoot = root,
            configuration = "Debug",
            projects
        }));
        return path;
    }
}

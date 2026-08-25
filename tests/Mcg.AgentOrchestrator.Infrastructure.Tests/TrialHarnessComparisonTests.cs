using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class TrialHarnessComparisonTests
{
    [Xunit.Fact]
    public void RunCreatesOneRootPerHarnessAtTheSameBaseCommit()
    {
        using var fixture = new Fixture();
        var host = new FakeTrialRootHost(fixture.Root);

        var result = new TrialHarnessComparison(host).Run(fixture.Request(
            new("alpha", "alpha.exe", ["one"], new Dictionary<string, string?> { ["HARNESS"] = "alpha" }),
            new("beta", "beta.exe", ["two"], new Dictionary<string, string?> { ["HARNESS"] = "beta" })));

        Xunit.Assert.True(result.Succeeded);
        Xunit.Assert.Equal(2, host.Requests.Count);
        Xunit.Assert.All(host.Requests, request => Xunit.Assert.Equal(fixture.BaseCommit, request.BaseCommit));
        Xunit.Assert.Equal(2, host.Requests.Select(request => request.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Xunit.Assert.NotSame(host.Requests[0].ExtraEnvironment, host.Requests[1].ExtraEnvironment);
    }

    [Xunit.Fact]
    public void RunInjectsIdenticalCanonicalWorkloadAndDistinctArmIdentities()
    {
        using var fixture = new Fixture();
        var host = new FakeTrialRootHost(fixture.Root);

        var result = new TrialHarnessComparison(host).Run(fixture.Request(
            new("alpha", "alpha.exe", []),
            new("beta", "beta.exe", [])));

        Xunit.Assert.True(result.Succeeded);
        Xunit.Assert.NotNull(result.WorkloadIdentity);
        Xunit.Assert.Single(result.Harnesses.Select(item => item.WorkloadIdentity?.Value).Distinct());
        Xunit.Assert.All(result.Harnesses, item => Xunit.Assert.Equal(result.WorkloadIdentity, item.WorkloadIdentity));
        Xunit.Assert.Equal(2, result.Harnesses.Select(item => item.ArmIdentity?.Value).Distinct().Count());
        Xunit.Assert.Equal(2, host.BriefPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Xunit.Assert.All(host.BriefBytes.Values, bytes => Xunit.Assert.Equal(fixture.BriefBytes, bytes));
        Xunit.Assert.All(host.LaunchEnvironments.Values, environment =>
        {
            Xunit.Assert.Equal(fixture.BaseCommit, environment["MCG_TRIAL_BASE_COMMIT"]);
            Xunit.Assert.Equal(fixture.Workload.BriefDigest, environment["MCG_TRIAL_BRIEF_SHA256"]);
            Xunit.Assert.Equal(fixture.Workload.ModelIdentity, environment["MCG_TRIAL_MODEL_IDENTITY"]);
            Xunit.Assert.Equal(result.WorkloadIdentity!.Value, environment["MCG_TRIAL_WORKLOAD_ID"]);
        });
        Xunit.Assert.All(result.Harnesses, item =>
            Xunit.Assert.Contains(item.WorkloadIdentity!.Value, File.ReadAllText(item.ReceiptPath), StringComparison.Ordinal));
        Xunit.Assert.NotNull(result.HistoricalTiming);
        Xunit.Assert.All(result.Harnesses, item => Xunit.Assert.NotNull(item.HistoricalTiming));
    }

    [Xunit.Fact]
    public void ProvisioningFailureReceiptRetainsFinalizedIdentities()
    {
        using var fixture = new Fixture();
        var host = new FakeTrialRootHost(fixture.Root);
        host.ThrowOnAddEnvironment.Add("alpha");

        var result = new TrialHarnessComparison(host).Run(fixture.Request(
            new("alpha", "alpha.exe", []),
            new("beta", "beta.exe", [])));

        var alpha = result.Harnesses.Single(item => item.Name == "alpha");
        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.NotNull(alpha.WorkloadIdentity);
        Xunit.Assert.NotNull(alpha.ArmIdentity);
        var receipt = File.ReadAllText(alpha.ReceiptPath);
        Xunit.Assert.Contains(alpha.WorkloadIdentity.Value, receipt, StringComparison.Ordinal);
        Xunit.Assert.Contains(alpha.ArmIdentity.Value, receipt, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void DurableReceiptsExcludeHistoricalBriefText()
    {
        const string secret = "TRIAL-BRIEF-SECRET-7d4b086ad17c";
        using var fixture = new Fixture();
        var host = new FakeTrialRootHost(fixture.Root);
        var workload = fixture.Workload with
        {
            BriefContent = secret,
            BriefDigest = TrialIdentity.ComputeBriefDigest(secret),
            HistoricalTiming = fixture.HistoricalTiming with { Objective = secret }
        };

        var result = new TrialHarnessComparison(host).Run(fixture.Request(
            new("alpha", "alpha.exe", []),
            new("beta", "beta.exe", [])) with { Workload = workload });

        Xunit.Assert.True(result.Succeeded);
        foreach (var receiptPath in result.Harnesses.Select(item => item.ReceiptPath).Append(result.ReceiptPath))
        {
            var receipt = File.ReadAllText(receiptPath);
            Xunit.Assert.DoesNotContain(secret, receipt, StringComparison.Ordinal);
            Xunit.Assert.Contains(workload.BriefDigest, receipt, StringComparison.Ordinal);
        }
    }

    [Xunit.Fact]
    public void DurableReceiptsDoNotPersistHarnessOutputThatEchoesBrief()
    {
        const string secret = "TRIAL-BRIEF-OUTPUT-SECRET-8224d42e9f6a";
        using var fixture = new Fixture();
        var host = new FakeTrialRootHost(fixture.Root);
        host.StdoutContent["alpha"] = $"{secret}\n{SuccessfulWorkerResult}";
        host.StderrContent["alpha"] = secret;
        var workload = fixture.Workload with
        {
            BriefContent = secret,
            BriefDigest = TrialIdentity.ComputeBriefDigest(secret)
        };

        var result = new TrialHarnessComparison(host).Run(fixture.Request(
            new("alpha", "alpha.exe", []),
            new("beta", "beta.exe", [])) with { Workload = workload });

        Xunit.Assert.True(result.Succeeded);
        var alpha = result.Harnesses.Single(item => item.Name == "alpha");
        Xunit.Assert.Equal(TrialWorkerResultStatus.Valid, alpha.WorkerResult.Status);
        Xunit.Assert.NotNull(alpha.StandardOutput);
        Xunit.Assert.NotNull(alpha.StandardError);
        Xunit.Assert.Equal(
            System.Text.Encoding.UTF8.GetByteCount(host.StdoutContent["alpha"]),
            alpha.StandardOutput.ByteCount);
        Xunit.Assert.Equal(ComputeSha256(host.StdoutContent["alpha"]), alpha.StandardOutput.Sha256);
        Xunit.Assert.Equal(ComputeSha256(host.StderrContent["alpha"]), alpha.StandardError.Sha256);
        var durableFiles = Directory.GetFiles(result.ReceiptDirectory, "*", SearchOption.AllDirectories);
        Xunit.Assert.DoesNotContain(durableFiles, path =>
            Path.GetFileName(path) is "stdout.log" or "stderr.log");
        Xunit.Assert.All(durableFiles, path =>
            Xunit.Assert.DoesNotContain(secret, File.ReadAllText(path), StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void MalformedWorkerResultRetainsOnlyTypedFailureAndOutputMetadata()
    {
        const string secret = "TRIAL-MALFORMED-OUTPUT-SECRET-9acf8e81b452";
        const string malformed = "WORKER_RESULT:\nblockers: none\nEND_WORKER_RESULT";
        using var fixture = new Fixture();
        var host = new FakeTrialRootHost(fixture.Root);
        host.StdoutContent["alpha"] = malformed;
        host.StderrContent["alpha"] = secret;
        host.ExitCodes["alpha"] = 23;

        var result = new TrialHarnessComparison(host).Run(fixture.Request(
            new("alpha", "alpha.exe", []),
            new("beta", "beta.exe", [])));

        var alpha = result.Harnesses.Single(item => item.Name == "alpha");
        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Equal(TrialHarnessOutcome.WorkerResultInvalid, alpha.Outcome);
        Xunit.Assert.Equal(23, alpha.ExitCode);
        Xunit.Assert.Equal(TrialWorkerResultStatus.Malformed, alpha.WorkerResult.Status);
        Xunit.Assert.Equal(0, alpha.WorkerResult.ParsedFieldCount);
        Xunit.Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(malformed), alpha.StandardOutput?.ByteCount);
        Xunit.Assert.Equal(ComputeSha256(malformed), alpha.StandardOutput?.Sha256);
        Xunit.Assert.Equal(ComputeSha256(secret), alpha.StandardError?.Sha256);
        var durableFiles = Directory.GetFiles(result.ReceiptDirectory, "*", SearchOption.AllDirectories);
        Xunit.Assert.DoesNotContain(durableFiles, path =>
            Path.GetFileName(path) is "stdout.log" or "stderr.log");
        Xunit.Assert.All(durableFiles, path =>
            Xunit.Assert.DoesNotContain(secret, File.ReadAllText(path), StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void WorkloadIdentityRecordsButDoesNotKeyOnSourceProvenance()
    {
        using var fixture = new Fixture();
        var first = TrialIdentity.CreateWorkload(fixture.Workload, fixture.BaseCommit);
        var second = TrialIdentity.CreateWorkload(
            fixture.Workload with { SourceProvenance = "historical:another-goal" },
            fixture.BaseCommit);

        Xunit.Assert.Equal(first.Value, second.Value);
        Xunit.Assert.NotEqual(first.SourceProvenance, second.SourceProvenance);
    }

    [Xunit.Fact]
    public void RunWritesPerHarnessOutputMetadataThatSurvivesTeardown()
    {
        using var fixture = new Fixture();
        var host = new FakeTrialRootHost(fixture.Root);
        host.ExitCodes["alpha"] = 3;
        host.ExitCodes["beta"] = 7;

        var result = new TrialHarnessComparison(host).Run(fixture.Request(
            new("alpha", "alpha.exe", []),
            new("beta", "beta.exe", [])));

        Xunit.Assert.True(result.Succeeded);
        Xunit.Assert.Equal([3, 7], result.Harnesses.Select(item => item.ExitCode).ToArray());
        Xunit.Assert.All(result.Harnesses, item =>
        {
            Xunit.Assert.True(File.Exists(item.ReceiptPath));
            Xunit.Assert.NotNull(item.StandardOutput);
            Xunit.Assert.NotNull(item.StandardError);
            Xunit.Assert.Equal(TrialWorkerResultStatus.Valid, item.WorkerResult.Status);
            Xunit.Assert.Equal(64, item.StandardOutput.Sha256.Length);
            Xunit.Assert.Equal(64, item.StandardError.Sha256.Length);
        });
        Xunit.Assert.DoesNotContain(
            Directory.GetFiles(result.ReceiptDirectory, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path) is "stdout.log" or "stderr.log");
        Xunit.Assert.All(host.RootPaths, root => Xunit.Assert.False(Directory.Exists(root)));
    }

    [Xunit.Fact]
    public void RunTearsDownEveryRootWhenOneHarnessFails()
    {
        using var fixture = new Fixture();
        var host = new FakeTrialRootHost(fixture.Root);
        host.ExitCodes["alpha"] = 9;
        host.ThrowOnStart.Add("beta");

        var result = new TrialHarnessComparison(host).Run(fixture.Request(
            new("alpha", "alpha.exe", []),
            new("beta", "beta.exe", [])));

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Equal(9, result.Harnesses.Single(item => item.Name == "alpha").ExitCode);
        Xunit.Assert.Equal(TrialHarnessOutcome.LaunchFailed, result.Harnesses.Single(item => item.Name == "beta").Outcome);
        Xunit.Assert.All(host.RootPaths, root => Xunit.Assert.False(Directory.Exists(root)));
    }

    [Xunit.Fact]
    public void RunReportsProtectedPathModificationByName()
    {
        using var fixture = new Fixture();
        var protectedPath = Path.Combine(fixture.Source, "protected.txt");
        File.WriteAllText(protectedPath, "before");
        var host = new FakeTrialRootHost(fixture.Root) { ProtectedPathToMutate = protectedPath };

        var result = new TrialHarnessComparison(host).Run(fixture.RequestWithProtectedPaths(
            [protectedPath],
            new("alpha", "alpha.exe", []),
            new("beta", "beta.exe", [])));

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Equal("after", File.ReadAllText(protectedPath));
        Xunit.Assert.All(host.Requests, request => Xunit.Assert.Equal(new[] { protectedPath }, request.ProtectedPaths));
        Xunit.Assert.Contains(result.Failures, failure => failure.Contains(protectedPath, StringComparison.OrdinalIgnoreCase));
        Xunit.Assert.Contains(result.Harnesses, harness => harness.Outcome == TrialHarnessOutcome.ProtectedPathModified);
    }

    [Xunit.Fact]
    public void RunPreservesTimeoutOutcomeWhenTeardownIsAlsoUnclean()
    {
        using var fixture = new Fixture();
        var host = new FakeTrialRootHost(fixture.Root);
        host.TimeoutOnWait.Add("alpha");
        host.UncleanTeardown.Add("alpha");

        var result = new TrialHarnessComparison(host).Run(fixture.Request(
            new("alpha", "alpha.exe", []),
            new("beta", "beta.exe", [])));

        var alpha = result.Harnesses.Single(item => item.Name == "alpha");
        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Equal(TrialHarnessOutcome.TimedOut, alpha.Outcome);
        Xunit.Assert.Contains(alpha.Diagnostics, diagnostic => diagnostic.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        Xunit.Assert.Contains(alpha.Diagnostics, diagnostic => diagnostic.Contains("teardown was unclean", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact]
    public void RunPreservesTimeoutOutcomeWhileCaptureHandlesRemainOpen()
    {
        using var fixture = new Fixture();
        var host = new FakeTrialRootHost(fixture.Root);
        host.TimeoutOnWait.Add("alpha");
        host.HoldCaptureFilesOpen.Add("alpha");

        var result = new TrialHarnessComparison(host).Run(fixture.Request(
            new("alpha", "alpha.exe", []),
            new("beta", "beta.exe", [])));

        var alpha = result.Harnesses.Single(item => item.Name == "alpha");
        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Equal(TrialHarnessOutcome.TimedOut, alpha.Outcome);
        Xunit.Assert.NotNull(alpha.StandardOutput);
        Xunit.Assert.NotNull(alpha.StandardError);
        Xunit.Assert.Contains(alpha.Diagnostics, diagnostic => diagnostic.Contains("timed out", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact]
    public void RunReportsMissingCaptureInsteadOfReturningAnEmptyReceipt()
    {
        using var fixture = new Fixture();
        var host = new FakeTrialRootHost(fixture.Root);
        host.MissingStdout.Add("alpha");

        var result = new TrialHarnessComparison(host).Run(fixture.Request(
            new("alpha", "alpha.exe", []),
            new("beta", "beta.exe", [])));

        var alpha = result.Harnesses.Single(item => item.Name == "alpha");
        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Equal(TrialHarnessOutcome.ReceiptCaptureFailed, alpha.Outcome);
        Xunit.Assert.Null(alpha.StandardOutput);
        Xunit.Assert.Null(alpha.StandardError);
        Xunit.Assert.Contains(alpha.Diagnostics, diagnostic => diagnostic.Contains("capture file does not exist", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact]
    public void RunLeavesHarnessesNotAttemptedWhenResolvedCommitsDisagree()
    {
        using var fixture = new Fixture();
        var host = new FakeTrialRootHost(fixture.Root);
        host.ResolvedCommits["beta"] = "different-commit";

        var result = new TrialHarnessComparison(host).Run(fixture.Request(
            new("alpha", "alpha.exe", []),
            new("beta", "beta.exe", [])));

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.All(result.Harnesses, harness => Xunit.Assert.Equal(TrialHarnessOutcome.NotAttempted, harness.Outcome));
        Xunit.Assert.Contains(result.Failures, failure => failure.Contains("different base commits", StringComparison.OrdinalIgnoreCase));
        Xunit.Assert.All(host.RootPaths, root => Xunit.Assert.False(Directory.Exists(root)));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "mcg-trial-comparison-tests", Guid.NewGuid().ToString("N"));
            Source = Path.Combine(Root, "source");
            Receipts = Path.Combine(Root, "receipts");
            Directory.CreateDirectory(Source);
        }

        public string Root { get; }
        public string Source { get; }
        public string Receipts { get; }
        public string BaseCommit { get; } = "0123456789abcdef";
        public string Brief { get; } = "identical brief bytes\r\nkept exact";
        public byte[] BriefBytes => System.Text.Encoding.UTF8.GetBytes(Brief);
        public TrialWorkload Workload => new(
            "brief-v1",
            Brief,
            TrialIdentity.ComputeBriefDigest(Brief),
            "OpenAI/gpt-test",
            "historical:test",
            HistoricalTiming);
        public GoalTimingReportSnapshot HistoricalTiming => new(
            new GoalId("historical-goal"),
            "historical objective",
            null,
            "unavailable",
            null,
            "unavailable",
            null,
            null,
            null,
            null,
            null,
            null,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            0,
            0,
            [],
            null);

        public TrialComparisonRequest Request(params TrialHarnessSpec[] harnesses) =>
            new(Source, BaseCommit, Workload, harnesses, Receipts, Path.Combine(Root, "roots"), null, TimeSpan.FromSeconds(1));

        public TrialComparisonRequest RequestWithProtectedPaths(IReadOnlyList<string> protectedPaths, params TrialHarnessSpec[] harnesses) =>
            new(Source, BaseCommit, Workload, harnesses, Receipts, Path.Combine(Root, "roots"), protectedPaths, TimeSpan.FromSeconds(1));

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private const string SuccessfulWorkerResult = """
        WORKER_RESULT:
        files: none
        commands: none
        tests: pass - trial completed
        commit: none
        blockers: none
        model_fit: fake/test - adequate - test harness - deterministic
        skills: none
        confidence: high
        END_WORKER_RESULT
        """;

    private static string ComputeSha256(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed class FakeTrialRootHost(string fixtureRoot) : ITrialRootHost
    {
        public string FixtureRoot { get; } = fixtureRoot;
        public List<TrialRootRequest> Requests { get; } = [];
        public List<string> RootPaths { get; } = [];
        public Dictionary<string, int> ExitCodes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ResolvedCommits { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ThrowOnStart { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ThrowOnAddEnvironment { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TimeoutOnWait { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> HoldCaptureFilesOpen { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> UncleanTeardown { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> MissingStdout { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> StdoutContent { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> StderrContent { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, IReadOnlyDictionary<string, string?>> LaunchEnvironments { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> BriefPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, byte[]> BriefBytes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? ProtectedPathToMutate { get; init; }

        public ITrialRootSession Create(TrialRootRequest request)
        {
            Requests.Add(request);
            var name = request.Name ?? "unnamed";
            var root = Path.Combine(fixtureRoot, "fake-roots", $"{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(root, "harness"));
            RootPaths.Add(root);
            return new FakeSession(this, name, root, ResolvedCommits.GetValueOrDefault(name, request.BaseCommit));
        }

        private sealed class FakeSession(FakeTrialRootHost owner, string name, string root, string commit) : ITrialRootSession
        {
            public string RootPath => root;
            public string ResolvedBaseCommit => commit;
            public string HarnessStatePath => Path.Combine(root, "harness");

            public void AddEnvironment(IReadOnlyDictionary<string, string?> environment)
            {
                if (owner.ThrowOnAddEnvironment.Contains(name))
                {
                    throw new InvalidOperationException($"{name} environment provisioning failed");
                }

                var copy = new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase);
                owner.LaunchEnvironments[name] = copy;
                var briefPath = copy["MCG_TRIAL_BRIEF_PATH"]!;
                owner.BriefPaths[name] = briefPath;
                owner.BriefBytes[name] = File.ReadAllBytes(briefPath);
            }

            public ITrialLaunch Start(ProcessStartInfo command)
            {
                if (owner.ThrowOnStart.Contains(name))
                {
                    throw new InvalidOperationException($"{name} launch failed");
                }

                if (owner.ProtectedPathToMutate is not null && name.Equals("alpha", StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(owner.ProtectedPathToMutate, "after");
                }

                var stdout = Path.Combine(HarnessStatePath, "stdout.log");
                var stderr = Path.Combine(HarnessStatePath, "stderr.log");
                if (!owner.MissingStdout.Contains(name))
                {
                    File.WriteAllText(stdout, owner.StdoutContent.GetValueOrDefault(name, SuccessfulWorkerResult));
                }
                File.WriteAllText(stderr, owner.StderrContent.GetValueOrDefault(name, $"{name} stderr"));
                return new FakeLaunch(
                    stdout,
                    stderr,
                    owner.ExitCodes.GetValueOrDefault(name),
                    !owner.TimeoutOnWait.Contains(name),
                    owner.HoldCaptureFilesOpen.Contains(name));
            }

            public TrialTeardownReport Destroy()
            {
                var outsideWrites = owner.ProtectedPathToMutate is not null && name.Equals("alpha", StringComparison.OrdinalIgnoreCase)
                    ? [owner.ProtectedPathToMutate]
                    : Array.Empty<string>();
                var clean = !owner.UncleanTeardown.Contains(name);
                Directory.Delete(root, recursive: true);
                return new TrialTeardownReport(
                    root,
                    RootRemoved: true,
                    [],
                    [],
                    JobExitConfirmed: clean,
                    outsideWrites,
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromMilliseconds(1),
                    Path.Combine(owner.FixtureRoot, $"{name}.teardown.json"),
                    clean ? [] : ["job exit was not confirmed"]);
            }

            public void Dispose()
            {
            }
        }

        private sealed class FakeLaunch : ITrialLaunch
        {
            private readonly bool _exits;
            private readonly FileStream? _stdoutWriter;
            private readonly FileStream? _stderrWriter;

            public FakeLaunch(string stdout, string stderr, int exitCode, bool exits, bool holdCaptureFilesOpen)
            {
                StdoutPath = stdout;
                StderrPath = stderr;
                ExitCode = exitCode;
                _exits = exits;
                if (holdCaptureFilesOpen)
                {
                    _stdoutWriter = new FileStream(stdout, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    _stderrWriter = new FileStream(stderr, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                }
            }

            public string StdoutPath { get; }
            public string StderrPath { get; }
            public int ExitCode { get; }
            public bool WaitForExit(int milliseconds) => _exits;
            public void Dispose()
            {
                _stdoutWriter?.Dispose();
                _stderrWriter?.Dispose();
            }
        }
    }
}

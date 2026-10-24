using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: all receipts, ledger entries and occupancy claims live in a unique temporary root.
public sealed class RemoteLaneRedEvidenceTests : IDisposable
{
    private const string ExecutorId = "mcg-exec-1";
    private const string Lane = "infrastructure tests: Process spawning";
    private const string FirstTest = "RemoteTests.FirstFailure";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "remote-red-evidence-" + Guid.NewGuid().ToString("N"));
    private string AttemptFolder => Path.Combine(_root, "attempt");
    private string AttemptPrefix => Path.Combine(AttemptFolder, "summary");
    private string Retained(int index = 1) => Path.Combine(AttemptFolder,
        $"remote-red-mcg-exec-1-infrastructure-tests-process-spawning-{index}.trx");

    public RemoteLaneRedEvidenceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Retain_CopiesTwoFailureReceiptAndReportsFirstFailure()
    {
        var source = WriteTrx("fetched.trx", (FirstTest, "first message"), ("RemoteTests.SecondFailure", "second message"));
        var bytes = File.ReadAllBytes(source);

        var reason = Retain([source], 2);

        Assert.Equal(bytes, File.ReadAllBytes(Retained()));
        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.Equal($"exit=2 failed=2 first={FirstTest}: first message", reason);
        Assert.Single(Directory.GetFiles(AttemptFolder));
    }

    [Fact]
    public async Task RedCoordinatorResult_RetainsReceiptAndReportsFailureInLedgerAndEvent()
    {
        var source = WriteTrx("red.trx", (FirstTest, "remote failure"));
        var (outcome, row, events) = await RunRemoteAsync(1, [source]);

        Assert.Null(outcome.Accepted);
        Assert.Equal(RemoteLaneOutcomeCode.RemoteRed, row.Outcome);
        Assert.Equal($"exit=1 failed=1 first={FirstTest}: remote failure", row.Reason);
        Assert.Equal($"REMOTE_LANE executor=mcg-exec-1 lane=\"{Lane}\" attempt=attempt " +
            $"outcome=remote-red reason=exit=1 failed=1 first={FirstTest}: remote failure", Assert.Single(events));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Retained()));
    }

    [Fact]
    public async Task MissingCoordinatorReceipt_PreservesIncompleteOutcomeAndReportsMissing()
    {
        var (outcome, row, events) = await RunRemoteAsync(0, [Path.Combine(_root, "missing.trx")]);

        Assert.Null(outcome.Accepted);
        Assert.Equal(RemoteLaneOutcomeCode.TrxIncomplete, row.Outcome);
        Assert.Equal("exit=0 trx=missing", row.Reason);
        Assert.Contains("outcome=trx-incomplete reason=exit=0 trx=missing", Assert.Single(events));
        Assert.Empty(Directory.GetFiles(AttemptFolder));
    }

    [Fact]
    public void MissingReceipt_ReturnsMissingWithoutThrowingOrCopying()
    {
        string? reason = null;
        var exception = Record.Exception(() => reason = Retain([Path.Combine(_root, "missing.trx")], 7));

        Assert.Null(exception);
        Assert.Equal("exit=7 trx=missing", reason);
        Assert.Empty(Directory.GetFiles(AttemptFolder));
    }

    [Fact]
    public void LongMessage_IsCutToExactlyThreeHundredCharacters()
    {
        var source = WriteTrx("long.trx", (FirstTest, new string('x', 1000)));

        Assert.Equal($"exit=2 failed=1 first={FirstTest}: " + new string('x', 300), Retain([source], 2));
    }

    [Fact]
    public async Task AcceptedCoordinatorResult_KeepsNullReasonAndCreatesNoRedReceipt()
    {
        var source = WriteTrx("green.trx");
        var (outcome, row, events) = await RunRemoteAsync(0, [source]);

        Assert.NotNull(outcome.Accepted);
        Assert.True(outcome.Accepted.Passed);
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome);
        Assert.Null(row.Reason);
        Assert.Contains("outcome=accepted reason=none", Assert.Single(events));
        Assert.Empty(Directory.GetFiles(AttemptFolder, "remote-red-*.trx"));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Assert.Single(outcome.Accepted.TestResultPaths!)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("summary")]
    public void PrefixWithoutDirectory_CopiesNothingAndReturnsReason(string? prefix)
    {
        var source = WriteTrx("no-prefix.trx", (FirstTest, "failure"));

        Assert.Equal("exit=1 trx=missing", RemoteLaneRedEvidence.Retain([source], prefix, ExecutorId, Lane, 1));
        Assert.Equal(new[] { source }, Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(AttemptFolder));
    }

    [Fact]
    public void Retain_OverwritesExistingDestinationWithoutTemporaryDebris()
    {
        var source = WriteTrx("replacement.trx", (FirstTest, "replacement"));
        Directory.CreateDirectory(AttemptFolder);
        File.WriteAllText(Retained(), "stale receipt");

        Assert.Equal($"exit=1 failed=1 first={FirstTest}: replacement", Retain([source], 1));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Retained()));
        Assert.Equal(new[] { Retained() }, Directory.GetFiles(AttemptFolder));
    }

    [Fact]
    public void FailedCopy_DoesNotConsumeAnIndexOrHideLaterReceipt()
    {
        var locked = WriteTrx("locked.trx", ("Locked.Failure", "locked"));
        var source = WriteTrx("available.trx", (FirstTest, "available"));
        using var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal($"exit=1 failed=1 first={FirstTest}: available",
            Retain([Path.Combine(_root, "absent.trx"), locked, source], 1));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Retained()));
        Assert.Equal(new[] { Retained() }, Directory.GetFiles(AttemptFolder));
    }

    [Fact]
    public void FailedMove_DeletesTemporaryCopyAndReturnsMissing()
    {
        var source = WriteTrx("move.trx", (FirstTest, "failure"));
        Directory.CreateDirectory(Retained());

        Assert.Equal("exit=1 trx=missing", Retain([source], 1));
        Assert.Empty(Directory.GetFiles(AttemptFolder));
        Assert.True(Directory.Exists(Retained()));
    }

    [Fact]
    public void InvalidAttemptFolder_ReturnsMissingWithoutThrowing()
    {
        var source = WriteTrx("folder.trx", (FirstTest, "failure"));
        File.WriteAllText(AttemptFolder, "directory blocked by file");

        Assert.Equal("exit=1 trx=missing", Retain([source], 1));
        Assert.Equal("directory blocked by file", File.ReadAllText(AttemptFolder));
    }

    [Fact]
    public void UnusableReceipts_DoNotHideLaterFailure()
    {
        var malformed = Path.Combine(_root, "malformed.trx");
        File.WriteAllText(malformed, "not XML");
        var green = WriteTrx("green.trx");
        var failing = WriteTrx("failing.trx", (FirstTest, "later failure"));

        Assert.Equal($"exit=1 failed=1 first={FirstTest}: later failure", Retain([malformed, green, failing], 1));
        Assert.Equal(File.ReadAllBytes(malformed), File.ReadAllBytes(Retained(1)));
        Assert.Equal(File.ReadAllBytes(green), File.ReadAllBytes(Retained(2)));
        Assert.Equal(File.ReadAllBytes(failing), File.ReadAllBytes(Retained(3)));
        Assert.Equal(3, Directory.GetFiles(AttemptFolder).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoReadableFailures_ReturnsMissing(bool malformed)
    {
        var source = WriteTrx("no-failures.trx");
        if (malformed) File.WriteAllText(source, "not XML");

        Assert.Equal("exit=1 trx=missing", Retain([source], 1));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Retained()));
    }

    [Fact]
    public void BlankFailureIdentityAndMessage_UseStableReasonShape()
    {
        var source = WriteTrx("blank.trx", (" ", " "));

        Assert.Equal("exit=1 failed=1 first=unknown: ", Retain([source], 1));
    }

    [Fact]
    public void LongLaneName_RetainsBoundedReceiptAndReportsFirstFailure()
    {
        var lane = "reviewer focused evidence: infrastructure tests " + string.Join("|",
            Enumerable.Range(1, 40).Select(n => $"FullyQualifiedName~Class{n}Fixture"));
        Assert.True(lane.Length >= 1000);
        var source = WriteTrx("long-lane.trx", (FirstTest, "long lane failure"));

        var reason = RemoteLaneRedEvidence.Retain([source], AttemptPrefix, ExecutorId, lane, 2);

        Assert.Equal($"exit=2 failed=1 first={FirstTest}: long lane failure", reason);
        var retained = Assert.Single(Directory.GetFiles(AttemptFolder));
        var name = Path.GetFileName(retained);
        Assert.True(name.Length <= 200);
        Assert.StartsWith("remote-red-mcg-exec-1-", name);
        Assert.EndsWith("-1.trx", name);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(retained));
    }

    [Fact]
    public void LongLaneNamesSharingAPrefixRetainDistinctReceipts()
    {
        var shared = "reviewer focused evidence: infrastructure tests " + new string('x', 400);
        var firstLane = shared + string.Join("|",
            Enumerable.Range(1, 40).Select(n => $"FullyQualifiedName~AlphaClass{n}Fixture"));
        var secondLane = shared + string.Join("|",
            Enumerable.Range(1, 40).Select(n => $"FullyQualifiedName~BetaClass{n}Fixture"));
        Assert.True(firstLane.Length >= 1000);
        Assert.True(secondLane.Length >= 1000);
        Assert.Equal(firstLane[..400], secondLane[..400]);
        Assert.NotEqual(firstLane, secondLane);
        var firstSource = WriteTrx("first-long-lane.trx", (FirstTest, "first lane failure"));
        var secondSource = WriteTrx("second-long-lane.trx", ("RemoteTests.SecondFailure", "second lane failure"));

        var firstReason = RemoteLaneRedEvidence.Retain([firstSource], AttemptPrefix, ExecutorId, firstLane, 2);
        var secondReason = RemoteLaneRedEvidence.Retain([secondSource], AttemptPrefix, ExecutorId, secondLane, 2);

        Assert.Equal($"exit=2 failed=1 first={FirstTest}: first lane failure", firstReason);
        Assert.Equal("exit=2 failed=1 first=RemoteTests.SecondFailure: second lane failure", secondReason);
        var retained = Directory.GetFiles(AttemptFolder);
        Assert.Equal(2, retained.Length);
        Assert.Equal(2, retained.Select(Path.GetFileName).Distinct().Count());
        Assert.All(retained, path => Assert.True(Path.GetFileName(path).Length <= 200));
        Assert.Single(retained, path => File.ReadAllBytes(firstSource).SequenceEqual(File.ReadAllBytes(path)));
        Assert.Single(retained, path => File.ReadAllBytes(secondSource).SequenceEqual(File.ReadAllBytes(path)));
    }

    private string Retain(IReadOnlyList<string> sources, int exitCode) =>
        RemoteLaneRedEvidence.Retain(sources, AttemptPrefix, ExecutorId, Lane, exitCode);

    private string WriteTrx(string fileName, params (string Name, string Message)[] failures)
    {
        var path = Path.Combine(_root, fileName);
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var results = failures.Length == 0
            ? new[] { new XElement(ns + "UnitTestResult", new XAttribute("testName", "RemoteTests.Pass"), new XAttribute("outcome", "Passed")) }
            : failures.Select(failure => new XElement(ns + "UnitTestResult",
                new XAttribute("testName", failure.Name), new XAttribute("outcome", "Failed"),
                new XElement(ns + "Output", new XElement(ns + "ErrorInfo", new XElement(ns + "Message", failure.Message))))).ToArray();
        new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results", results),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", failures.Length == 0 ? "Completed" : "Failed"),
                new XElement(ns + "Counters", new XAttribute("total", results.Length), new XAttribute("executed", results.Length),
                    new XAttribute("passed", failures.Length == 0 ? 1 : 0), new XAttribute("failed", failures.Length),
                    new XAttribute("notExecuted", 0))))).Save(path);
        return path;
    }

    private async Task<(RemoteLaneOutcome Outcome, RemoteExecutorHealthRecord Row, List<string> Events)> RunRemoteAsync(
        int exitCode, IReadOnlyList<string> paths)
    {
        var clock = new ManualRemoteLaneClock();
        var entry = new RemoteLaneExecutorEntry(ExecutorId, 60);
        var check = new Check
        {
            Name = Lane,
            Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~RemoteTests"]
        };
        var fake = new FakeRemoteLaneExecutor
        {
            Submit = (request, _) =>
            {
                var handle = new FakeRemoteLaneExecutor.Handle(clock.GetUtcNow());
                handle.Publish(new(request.ExecutorId, request.Lane, request.FilterHash, request.VerifyingCommitSha,
                    request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, exitCode, paths));
                return Task.FromResult(new RemoteLaneSubmission(handle));
            }
        };
        var events = new List<string>();
        using var coordinator = new RemoteLaneCoordinator(new([entry], [Lane], null),
            new RemoteLaneCandidateIdentity("attempt", "goal", "commit", "tree", "main", "manifest"),
            _root, AttemptPrefix, fake, clock, TimeSpan.FromSeconds(1), null, events.Add);
        Assert.True(coordinator.IsEligible(check));
        Assert.Equal(entry, coordinator.TryClaimIdleExecutor());

        var outcome = await coordinator.RunRemoteAsync(check, entry, TimeSpan.FromMinutes(1), CancellationToken.None);

        var request = Assert.Single(fake.Requests);
        Assert.Equal("FullyQualifiedName~RemoteTests", request.Filter);
        Assert.Equal(ExecutorId, request.ExecutorId);
        return (outcome, Assert.Single(RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(_root))), events);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

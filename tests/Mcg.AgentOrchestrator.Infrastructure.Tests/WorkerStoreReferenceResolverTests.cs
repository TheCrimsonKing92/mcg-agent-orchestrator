using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

public sealed class WorkerStoreReferenceResolverTests
{
    [Fact]
    public void TrxAndAttemptResultFilesContainOnlySelectedExcerpts()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        fixture.WriteSource("acceptance-gate-attempts/g/run.trx", """
            <TestRun><Results>
              <UnitTestResult testName="Selected.Failure" outcome="Failed"><Output><ErrorInfo>
                <Message>selected message&#10;second message line</Message>
                <StackTrace>at Selected.First()&#10;at Selected.Second()</StackTrace>
              </ErrorInfo></Output></UnitTestResult>
              <UnitTestResult testName="Other.Failure" outcome="Failed"><Output><ErrorInfo><Message>Selected occurs only in message</Message></ErrorInfo></Output></UnitTestResult>
              <UnitTestResult testName="Selected.Passing" outcome="Passed" />
            </Results></TestRun>
            """);
        fixture.WriteSource("grouped-gate-attempts/g/a.result.json", "{\"outcome\":{\"exitCode\":7},\"unselected\":\"secret\"}");
        var context = fixture.WriteContext("Read selected failures.\n"
            + "store-ref: trx = trx:acceptance-gate-attempts/g/run.trx#test=Selected\n"
            + "store-ref: json = attempt-result:grouped-gate-attempts/g/a.result.json#outcome.exitCode");

        Assert.Equal("[FAIL] Selected.Failure: selected message | at Selected.First()",
            WorkerStoreReferenceFixture.Excerpt(File.ReadAllText(Path.Combine(context, "store-refs", "trx.md"))));
        Assert.Equal("7", WorkerStoreReferenceFixture.Excerpt(File.ReadAllText(Path.Combine(context, "store-refs", "json.md"))));
    }

    [Fact]
    public void OversizedSelectedJsonExcerptIsCappedWithOriginalLengthRecorded()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        var selected = JsonSerializer.Serialize(new string('x', WorkerStoreReferenceResolver.MaxExcerptChars + 31));
        var path = fixture.WriteSource("operator-evidence/a.attempt.json", "{\"selected\":" + selected + ",\"other\":42}");
        var context = fixture.WriteContext("Read excerpt.\nstore-ref: capped = attempt-result:operator-evidence/a.attempt.json#selected");
        var content = File.ReadAllText(Path.Combine(context, "store-refs", "capped.md"));

        Assert.Equal(selected[..WorkerStoreReferenceResolver.MaxExcerptChars], WorkerStoreReferenceFixture.Excerpt(content));
        Assert.Equal(WorkerStoreReferenceResolver.MaxExcerptChars, WorkerStoreReferenceFixture.Excerpt(content).Length);
        Assert.Contains($"Truncated: yes; original-chars={selected.Length}; limit-chars={WorkerStoreReferenceResolver.MaxExcerptChars}", content);
        Assert.Contains($"Source sha256: {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()}", content);
    }

    [Fact]
    public void GoalEventsTypeAndContainsSelectorsIntersectInSourceOrder()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        const string first = "{\"eventType\":\"Criterion\",\"text\":\"needle first\"}";
        const string second = "{\"eventType\":\"Criterion\",\"text\":\"needle second\"}";
        fixture.WriteSource($"goal-events/{WorkerStoreReferenceFixture.EventGoalId}.jsonl",
            first + "\n{\"eventType\":\"Other\",\"text\":\"needle\"}\n" + second
            + "\n{\"eventType\":\"Criterion\",\"text\":\"other\"}\n");

        var result = fixture.Resolve($"store-ref: events = goal-events:{WorkerStoreReferenceFixture.EventGoalId}#type=Criterion&contains=needle");

        Assert.Null(result.ReasonCode);
        Assert.Equal(first + Environment.NewLine + second, WorkerStoreReferenceFixture.Excerpt(result.Content!));
        var typeOnly = fixture.Resolve($"store-ref: events = goal-events:{WorkerStoreReferenceFixture.EventGoalId}#type=Other");
        Assert.Equal("{\"eventType\":\"Other\",\"text\":\"needle\"}", WorkerStoreReferenceFixture.Excerpt(typeOnly.Content!));
    }

    [Fact]
    public void DottedJsonSelectorCanIndexArrays()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        fixture.WriteSource("pre-review-evidence-attempts/a.attempt.json", "{\"checks\":[{\"status\":false},{\"status\":true}]}");
        var result = fixture.Resolve("store-ref: array = attempt-result:pre-review-evidence-attempts/a.attempt.json#checks.1.status");
        Assert.Null(result.ReasonCode);
        Assert.Equal("true", WorkerStoreReferenceFixture.Excerpt(result.Content!));
    }

    [Theory]
    [InlineData("state-db:state.db", "refused-kind")]
    [InlineData("environment:PATH", "refused-kind")]
    [InlineData("operator-evidence:state.db", "outside-root")]
    [InlineData("operator-evidence:operator-evidence/../state.db", "parent-traversal")]
    [InlineData("operator-evidence:operator-evidence\\..\\state.db", "parent-traversal")]
    [InlineData("operator-evidence:/etc/passwd", "absolute-path")]
    [InlineData("operator-evidence:C:\\Users\\x\\file.md", "absolute-path")]
    [InlineData("operator-evidence:\\\\server\\share\\file.md", "absolute-path")]
    [InlineData("operator-evidence:operator-evidence/x.md:stream", "absolute-path")]
    [InlineData("operator-evidence:operator-evidence./x.md", "invalid-locator")]
    [InlineData("operator-evidence:operator-evidence/x.md#execute=anything", "invalid-selector")]
    [InlineData("trx:acceptance-gate-attempts/x.trx", "invalid-selector")]
    [InlineData("attempt-result:acceptance-gate-attempts/state.db#field", "invalid-locator")]
    [InlineData("attempt-result:acceptance-gate-attempts/a.result.json#field..nested", "invalid-selector")]
    [InlineData("goal-events:short#contains=x", "invalid-locator")]
    [InlineData("cohort-receipt:../receipt", "parent-traversal")]
    [InlineData("train-receipt:identity#field", "invalid-selector")]
    public void StaticRefusalsAreDeterministicEvenWithoutAStoreRoot(string target, string reason)
    {
        var reference = Assert.Single(WorkerStoreReferenceResolver.Parse([$"store-ref: denied = {target}"]));
        var result = WorkerStoreReferenceResolver.Resolve(reference, null, new FixtureClock(), out _);
        Assert.Null(result.Content);
        Assert.Equal(reason, result.ReasonCode);
    }

    [Fact]
    public void ParserAnchorsLinesAndReportsMalformedNamesAndDuplicates()
    {
        var references = WorkerStoreReferenceResolver.Parse([
            "A quoted store-ref: ignored = operator-evidence:operator-evidence/x.md\n"
            + "store-ref: broken = no-kind\n"
            + "store-ref: ../unsafe = operator-evidence:operator-evidence/x.md\n"
            + "store-ref: valid = operator-evidence:operator-evidence/x.md\n"
            + "store-ref: valid = operator-evidence:operator-evidence/y.md"]);
        Assert.Equal(4, references.Count);
        Assert.Equal("parse-error", references[0].Error);
        Assert.Equal("invalid-name", references[1].Error);
        Assert.Equal("invalid", references[1].Name);
        Assert.Null(references[2].Error);
        Assert.Equal("duplicate-name", references[3].Error);
    }

    [Fact]
    public void MissingEmptyUnmatchedAndMalformedRecordsReportFixedReasons()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        fixture.WriteSource("operator-evidence/empty.md", "");
        fixture.WriteSource("operator-evidence/a.result.json", "{\"found\":42}");
        fixture.WriteSource("operator-evidence/b.result.json", "not json");
        fixture.WriteSource($"goal-events/{WorkerStoreReferenceFixture.EventGoalId}.jsonl", "{\"eventType\":\"Other\"}");
        var cases = new[]
        {
            ("operator-evidence:operator-evidence/missing.md", "source-missing"),
            ("operator-evidence:operator-evidence/empty.md", "no-match"),
            ("attempt-result:operator-evidence/a.result.json#missing", "no-match"),
            ("attempt-result:operator-evidence/b.result.json#missing", "read-failed"),
            ($"goal-events:{WorkerStoreReferenceFixture.EventGoalId}#type=Missing", "no-match")
        };
        foreach (var (target, reason) in cases)
        {
            var result = fixture.Resolve($"store-ref: selected = {target}");
            Assert.Null(result.Content);
            Assert.Equal(reason, result.ReasonCode);
        }
    }

    [Theory]
    [InlineData("cohort-receipt", "cohort-acceptance.db", "cohort_receipts", "cohort_id")]
    [InlineData("train-receipt", "merge-train-acceptance.db", "merge_train_receipts", "train_id")]
    public void ReceiptLookupReturnsOnlyNamedRowAndDoesNotModifyDatabase(string kind, string file, string table, string idColumn)
    {
        using var fixture = new WorkerStoreReferenceFixture();
        var path = Path.Combine(fixture.StoreRoot, file);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE {table}({idColumn} TEXT PRIMARY KEY, receipt_id TEXT, payload_json TEXT);"
                + $"INSERT INTO {table} VALUES ('named', 'receipt-1', '{{\"selected\":true}}'), ('other', 'receipt-2', '{{\"selected\":false}}');";
            command.ExecuteNonQuery();
        }
        var originalBytes = File.ReadAllBytes(path);
        var result = fixture.Resolve($"store-ref: row = {kind}:named");
        Assert.Null(result.ReasonCode);
        using var row = JsonDocument.Parse(WorkerStoreReferenceFixture.Excerpt(result.Content!));
        Assert.Equal("named", row.RootElement.GetProperty(idColumn).GetString());
        Assert.Equal("receipt-1", row.RootElement.GetProperty("receipt_id").GetString());
        Assert.Equal("{\"selected\":true}", row.RootElement.GetProperty("payload_json").GetString());
        Assert.Equal(3, row.RootElement.EnumerateObject().Count());
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        Assert.Equal("no-match", fixture.Resolve($"store-ref: row = {kind}:missing").ReasonCode);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void PerFileTotalAndReferenceCountLimitsReportEveryOmittedReference()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        fixture.WriteSource("operator-evidence/large.md", new string('x', WorkerStoreReferenceResolver.MaxExcerptChars + 1));
        var lines = Enumerable.Range(0, 5).Select(index => $"store-ref: r{index} = operator-evidence:operator-evidence/large.md");
        var context = fixture.WriteContext("Read bounded evidence.\n" + string.Join(Environment.NewLine, lines));
        Assert.Equal(4, Directory.GetFiles(Path.Combine(context, "store-refs")).Length);
        Assert.Contains("STORE_REF_UNRESOLVED name=r4 reason=limit-exceeded", File.ReadAllText(Path.Combine(context, "manifest.md")));
        fixture.WriteSource("operator-evidence/small.md", "small");
        lines = Enumerable.Range(0, WorkerStoreReferenceResolver.MaxReferencesPerDispatch + 1)
            .Select(index => $"store-ref: r{index} = operator-evidence:operator-evidence/small.md");
        context = fixture.WriteContext("Read bounded evidence.\n" + string.Join(Environment.NewLine, lines));
        Assert.Equal(WorkerStoreReferenceResolver.MaxReferencesPerDispatch, Directory.GetFiles(Path.Combine(context, "store-refs")).Length);
        Assert.Contains($"STORE_REF_UNRESOLVED name=r{WorkerStoreReferenceResolver.MaxReferencesPerDispatch} reason=limit-exceeded",
            File.ReadAllText(Path.Combine(context, "manifest.md")));
    }

    private sealed class FixtureClock : Mcg.AgentOrchestrator.Core.IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 4, 2, 0, 0, TimeSpan.Zero);
    }
}

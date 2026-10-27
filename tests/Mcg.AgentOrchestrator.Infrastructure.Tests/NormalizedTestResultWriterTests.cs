using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using TestResult = Mcg.AgentOrchestrator.Infrastructure.TestResult;

// Parallel-safe: every file operation is isolated in a unique temporary directory.
public sealed class NormalizedTestResultWriterTests
{
    private const string SpecialText = "quote\" slash\\ newline\n tab\t é \U0001F600";

    [Theory]
    [InlineData("empty")]
    [InlineData("minimal")]
    [InlineData("complete")]
    [InlineData("statuses")]
    [InlineData("escaped")]
    [InlineData("empty-message")]
    [InlineData("provenance")]
    [InlineData("random")]
    public void Write_SupportedFields_RoundTripsEveryFieldInOrder(string scenario)
    {
        var expected = RecordsFor(scenario);
        WithRoot(root =>
        {
            var path = Path.Combine(root, "result.ctrf.json");
            NormalizedTestResultWriter.Write(path, "writer-test", expected);

            var read = new NormalizedTestResultSource().Read(path);

            Assert.Equal(TestResultReadStatus.Readable, read.Status);
            Assert.Equal(expected.Count, read.Records.Count);
            for (var index = 0; index < expected.Count; index++)
                Assert.Empty(Differences(expected[index], read.Records[index]));
            if (scenario == "empty-message") Assert.Equal("", Assert.Single(read.Records).Message);
            if (scenario == "complete") Assert.Equal(9007199254740993L, Assert.Single(read.Records).DurationMs);
            if (scenario == "provenance") Assert.Equal("original-reporter", Assert.Single(read.Records).ReportedBy);
        });
    }

    [Fact]
    public void Differences_ExtraSuiteElement_ReportsSuitePath()
    {
        var record = Minimal("control") with { SuitePath = ["suite"] };

        Assert.Equal(new[] { "SuitePath" }, Differences(record, record with { SuitePath = ["suite", "extra"] }));
    }

    [Fact]
    public void Serialize_CompleteAndMinimalRecords_UsesPinnedShapeAndOmitsNulls()
    {
        using var document = JsonDocument.Parse(NormalizedTestResultWriter.Serialize("writer-test",
            [Complete("full"), Minimal("minimal"), Minimal("empty") with { Message = "" }]));
        var root = document.RootElement;
        Assert.Equal("CTRF", root.GetProperty("reportFormat").GetString());
        Assert.Equal(NormalizedTestResultSource.SchemaVersion, root.GetProperty("schemaVersion").GetString());
        var results = root.GetProperty("results");
        Assert.Equal("writer-test", results.GetProperty("tool").GetProperty("name").GetString());
        var tests = results.GetProperty("tests").EnumerateArray().ToArray();
        Assert.All(tests, test => Assert.All(test.EnumerateObject(), property =>
            Assert.Contains(property.Name, NormalizedTestResultSource.TestPropertyNames)));
        Assert.All(tests, test => Assert.Equal(JsonValueKind.Array, test.GetProperty("suite").ValueKind));
        Assert.Equal(9007199254740993L, tests[0].GetProperty("duration").GetInt64());
        Assert.Equal(2, tests[0].GetProperty("retryAttempts").GetArrayLength());
        foreach (var property in new[] { "duration", "filePath", "message", "trace", "retryAttempts" })
            Assert.False(tests[1].TryGetProperty(property, out _));
        Assert.Empty(tests[1].GetProperty("suite").EnumerateArray());
        var extra = tests[1].GetProperty("extra");
        Assert.Equal("writer-test", extra.GetProperty("reportedBy").GetString());
        Assert.False(extra.TryGetProperty("seed", out _));
        Assert.False(extra.TryGetProperty("outputRef", out _));
        Assert.Equal("", tests[2].GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("duplicate")]
    public void Serialize_InvalidIdentity_ThrowsArgumentException(string scenario)
    {
        IReadOnlyList<TestResult> records = scenario switch
        {
            "id" => [Minimal("")],
            "name" => [Minimal("empty-name") with { DisplayName = "" }],
            "duplicate" => [Minimal("dup"), Minimal("dup")],
            _ => [Minimal("valid")]
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            NormalizedTestResultWriter.Serialize(scenario == "tool" ? "" : "tool", records));

        if (scenario == "duplicate") Assert.Contains("dup", exception.Message);
        if (scenario == "name") Assert.Contains("empty-name", exception.Message);
    }

    [Fact]
    public void Serialize_OrdinalIdsAndWhitespace_PreservesNonemptyStrings()
    {
        using var document = JsonDocument.Parse(NormalizedTestResultWriter.Serialize(" ",
            [Minimal("case"), Minimal("CASE"), Minimal(" ") with { DisplayName = "\t" }]));

        Assert.Equal(new[] { "case", "CASE", " " }, document.RootElement.GetProperty("results")
            .GetProperty("tests").EnumerateArray().Select(test => test.GetProperty("testId").GetString()));
    }

    [Fact]
    public void PathFor_MultipleExtensions_ReplacesOnlyLastExtension()
    {
        WithRoot(root => Assert.Equal(Path.Combine(root, "attempt.go-test.ctrf.json"),
            NormalizedTestResultWriter.PathFor(Path.Combine(root, "attempt.go-test.trx"))));
    }

    [Fact]
    public void Write_DirectoryTarget_PreservesDirectoryAndRemovesTemporaryFile()
    {
        WithRoot(root =>
        {
            var target = Path.Combine(root, "result.ctrf.json");
            Directory.CreateDirectory(target);

            var exception = Record.Exception(() => NormalizedTestResultWriter.Write(target, "tool", [Minimal("one")]));

            Assert.True(exception is IOException or UnauthorizedAccessException, exception?.ToString());
            Assert.True(Directory.Exists(target));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        });
    }

    [Fact]
    public void Write_ExistingTarget_ReplacesPreviousRecords()
    {
        WithRoot(root =>
        {
            var target = Path.Combine(root, "result.ctrf.json");
            NormalizedTestResultWriter.Write(target, "tool", [Minimal("one"), Minimal("two"), Minimal("three")]);
            var replacement = Complete("replacement");

            NormalizedTestResultWriter.Write(target, "tool", [replacement]);

            var read = new NormalizedTestResultSource().Read(target);
            Assert.Equal(TestResultReadStatus.Readable, read.Status);
            Assert.Empty(Differences(replacement, Assert.Single(read.Records)));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        });
    }

    [Fact]
    public void Write_InvalidRecords_PreservesTargetAndCleansTemporaryFile()
    {
        WithRoot(root =>
        {
            var target = Path.Combine(root, "result.ctrf.json");
            File.WriteAllText(target, "original");
            File.WriteAllText(target + ".tmp", "stale");

            Assert.Throws<ArgumentException>(() => NormalizedTestResultWriter.Write(target, "tool", [Minimal("")]));

            Assert.Equal("original", File.ReadAllText(target));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        });
    }

    private static TestResult Minimal(string id) => new(id, "test", null, [], TestResultStatus.Passed,
        null, null, null, [], null, null, "writer-test");

    private static TestResult Complete(string id) => new(id, "complete", "tests/sample.cs", ["suite", "child"],
        TestResultStatus.Failed, 9007199254740993L, "message", "trace",
        [new(TestResultStatus.Error, 0, "first", "first trace"),
            new(TestResultStatus.Passed, 9007199254740993L, "second", "second trace")],
        "seed", "output", "original-reporter");

    private static IReadOnlyList<TestResult> RecordsFor(string scenario) => scenario switch
    {
        "empty" => [],
        "minimal" => [Minimal("minimal")],
        "complete" => [Complete("complete")],
        "statuses" => Enum.GetValues<TestResultStatus>().Select(status =>
            Minimal(status.ToString()) with { Status = status, DurationMs = 0 }).ToArray(),
        "escaped" => [new(SpecialText, SpecialText, SpecialText, [SpecialText], TestResultStatus.Error,
            1, SpecialText, SpecialText, [new(TestResultStatus.Skipped, 2, SpecialText, SpecialText)],
            SpecialText, SpecialText, SpecialText)],
        "empty-message" => [Minimal("empty-message") with { Message = "" }],
        "provenance" => [Minimal("provenance") with { ReportedBy = "original-reporter" }],
        "random" => RandomRecords(),
        _ => throw new ArgumentException("Unknown scenario.", nameof(scenario))
    };

    private static IReadOnlyList<TestResult> RandomRecords()
    {
        var random = new Random(20261027);
        string?[] text = [null, "", "plain", SpecialText];
        long?[] durations = [null, 0, 1, 9007199254740993L, long.MaxValue];
        string? NextText() => text[random.Next(text.Length)];
        long? NextDuration() => durations[random.Next(durations.Length)];
        return Enumerable.Range(0, 200).Select(index => new TestResult($"t{index}", $"test {index} {NextText()}",
            NextText(), Enumerable.Range(0, random.Next(4)).Select(_ => NextText() ?? "suite").ToArray(),
            (TestResultStatus)random.Next(4), NextDuration(), NextText(), NextText(),
            Enumerable.Range(0, random.Next(3)).Select(_ => new TestResultAttempt(
                (TestResultStatus)random.Next(4), NextDuration(), NextText(), NextText())).ToArray(),
            NextText(), NextText(), NextText() ?? "writer-test")).ToArray();
    }

    private static IReadOnlyList<string> Differences(TestResult expected, TestResult actual)
    {
        var differences = new List<string>();
        if (expected.Id != actual.Id) differences.Add(nameof(TestResult.Id));
        if (expected.DisplayName != actual.DisplayName) differences.Add(nameof(TestResult.DisplayName));
        if (expected.File != actual.File) differences.Add(nameof(TestResult.File));
        if (!expected.SuitePath.SequenceEqual(actual.SuitePath)) differences.Add(nameof(TestResult.SuitePath));
        if (expected.Status != actual.Status) differences.Add(nameof(TestResult.Status));
        if (expected.DurationMs != actual.DurationMs) differences.Add(nameof(TestResult.DurationMs));
        if (expected.Message != actual.Message) differences.Add(nameof(TestResult.Message));
        if (expected.Trace != actual.Trace) differences.Add(nameof(TestResult.Trace));
        if (!expected.Attempts.Select(attempt => (attempt.Status, attempt.DurationMs, attempt.Message, attempt.Trace))
            .SequenceEqual(actual.Attempts.Select(attempt => (attempt.Status, attempt.DurationMs, attempt.Message, attempt.Trace))))
            differences.Add(nameof(TestResult.Attempts));
        if (expected.Seed != actual.Seed) differences.Add(nameof(TestResult.Seed));
        if (expected.OutputRef != actual.OutputRef) differences.Add(nameof(TestResult.OutputRef));
        if (expected.ReportedBy != actual.ReportedBy) differences.Add(nameof(TestResult.ReportedBy));
        return differences;
    }

    private static void WithRoot(Action<string> assertion)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try { assertion(root); }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}

using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Microsoft.Data.Sqlite;

// Parallel-safe: each fixture owns its stores, result files, captured output and headless GUI.
public sealed class OwnerConsoleJointGateFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Activity_CohortReceipt_NamesFirstFailingTestAndMessage(bool malformedChecks)
    {
        using var fixture = new Fixture();
        fixture.Trx("cohort.trx", "Example.ChecksTests.First", "first   failure\nsecond line");
        fixture.Cohort(checks: malformedChecks ? "malformed" : "[\"tests\"]", paths: "[\"cohort.trx\"]");
        var line = await fixture.Line("kind=cohort identity=c1 receipt=r1");
        Assert.Contains("ChecksTests.First: first failure", line);
        Assert.DoesNotContain("second line", line);
        Assert.DoesNotContain("has not been recorded", line);
    }

    [Fact]
    public async Task Activity_CohortIdentityAndLegacyPartition_UsesPartitionFailure()
    {
        using var fixture = new Fixture();
        fixture.Trx("partition.trx", "Example.PartitionTests.Broken", "partition failed");
        fixture.Cohort();
        fixture.Sql("cohort-acceptance.db", """
            CREATE TABLE cohort_partition_receipts(cohort_id TEXT, member_ordinal INTEGER, outcome TEXT, test_result_paths_json TEXT);
            INSERT INTO cohort_partition_receipts VALUES('c1',0,'Passed','[]'),('c1',1,'Failed','["partition.trx"]');
            """);
        Assert.Contains("PartitionTests.Broken: partition failed", await fixture.Line("kind=cohort identity=c1"));
    }

    [Fact]
    public async Task Activity_TrainEvidenceFolder_UsesFirstOrdinalResultBeforeBuildOrCheck()
    {
        using var fixture = new Fixture();
        fixture.Trx("merge-train-evidence/t1/01-later.trx", "Example.LaterTests.Second", "later failure");
        fixture.Trx("merge-train-evidence/t1/00-first.trx", "Example.TrainTests.First", "train failure\nsecond line");
        fixture.Train();
        fixture.BuildOutput();
        var line = await fixture.Line("kind=train identity=t1 receipt=r1 attempt=a1");
        Assert.Contains("TrainTests.First: train failure", line);
        Assert.DoesNotContain("LaterTests", line);
        Assert.DoesNotContain("error CS", line);
        Assert.DoesNotContain("second line", line);
    }

    [Theory]
    [InlineData("StdoutPath")]
    [InlineData("StderrPath")]
    public async Task Activity_TrainBuildOutput_NamesCompilerErrorBeforeFailedCheck(string outputField)
    {
        using var fixture = new Fixture();
        fixture.Train();
        fixture.BuildOutput(outputField);
        var line = await fixture.Line("kind=train identity=t1 receipt=r1 attempt=a1");
        Assert.Contains("GoalDispatchOperations.cs(483,77): error CS1061: missing member", line);
        Assert.DoesNotContain("[project.csproj]", line);
        Assert.DoesNotContain("second compiler error", line);
        Assert.DoesNotContain("check build exited", line);
    }

    [Fact]
    public async Task Activity_CohortOutputTail_NamesCompilerError()
    {
        using var fixture = new Fixture();
        fixture.Cohort(detail: "File.cs(1,2): error CS0103: unknown name\nFile.cs(3,4): error CS0103: later");
        var line = await fixture.Line("kind=cohort receipt=r1");
        Assert.Contains("File.cs(1,2): error CS0103: unknown name", line);
        Assert.DoesNotContain("later", line);
    }

    [Theory]
    [InlineData("cohort")]
    [InlineData("train")]
    public async Task Activity_CheckOnlyFailure_NamesCheckAndExitCode(string kind)
    {
        using var fixture = new Fixture();
        if (kind == "cohort") fixture.Cohort(checks: "[\"build\"]");
        else fixture.Train();
        Assert.Contains("failed (check build exited 1)", await fixture.Line("kind=" + kind + " receipt=r1"));
    }

    [Theory]
    [InlineData("cohort")]
    [InlineData("train")]
    public async Task Activity_MissingReceiptResultsAndOutput_KeepsFallbackAndCreatesNothing(string kind)
    {
        using var fixture = new Fixture();
        // An existing, unrelated store is not evidence for this attempt and must remain untouched.
        fixture.Cohort();
        var store = fixture.PathFor("cohort-acceptance.db");
        var before = File.ReadAllBytes(store);
        var files = Directory.GetFiles(fixture.Directory, "*", SearchOption.AllDirectories).Order().ToArray();
        var line = await fixture.Line("kind=" + kind + " identity=missing receipt=missing attempt=missing");
        Assert.Contains("the failure reason has not been recorded", line);
        Assert.Equal(before, File.ReadAllBytes(store));
        Assert.Equal(files, Directory.GetFiles(fixture.Directory, "*", SearchOption.AllDirectories).Order().ToArray());
        // Pin the boundary: a recorded receipt is a different case from a missing one.
        Assert.Contains("gate exited 1", await fixture.Line("kind=cohort receipt=r1"));
    }

    [Fact]
    public async Task Activity_RecordedResultsWithoutFailingTest_DoesNotClaimEvidenceWasNeverRecorded()
    {
        using var fixture = new Fixture();
        fixture.Write("merge-train-evidence/t1/00-results.trx", "<TestRun><Results/></TestRun>");
        var line = await fixture.Line("kind=train identity=t1");
        Assert.Contains("recorded gate evidence contains no failure detail", line);
        Assert.DoesNotContain("has not been recorded", line);
    }

    [Fact]
    public async Task Activity_TrainVerdictWithoutStore_NamesCheckAndExitCode()
    {
        using var fixture = new Fixture();
        fixture.Write("merge-train-evidence/t1/gate-verdict.json", """{"failedChecks":["build"],"gateExitCode":2}""");
        Assert.Contains("check build exited 2", await fixture.Line("kind=train identity=t1"));
    }

    [Fact]
    public async Task Activity_UnsafeEvidenceIdentity_DoesNotReadOutsideTrainFolder()
    {
        using var fixture = new Fixture();
        fixture.Trx("outside/00-results.trx", "Example.EscapeTests.Broken", "must not appear");
        var line = await fixture.Line("kind=train identity=../outside attempt=../outside");
        Assert.Contains("has not been recorded", line);
        Assert.DoesNotContain("EscapeTests", line);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Directory = SharedTestSupport.CreateTempDirectory();
        internal string PathFor(string path) => Path.Combine(Directory, path);
        internal void Write(string path, string text)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(PathFor(path))!);
            File.WriteAllText(PathFor(path), text);
        }
        internal void Trx(string path, string name, string message) => Write(path,
            $"<TestRun><Results><UnitTestResult testName=\"{name}\" outcome=\"Failed\"><Output><ErrorInfo><Message>{message}</Message></ErrorInfo></Output></UnitTestResult></Results></TestRun>");
        internal void Sql(string database, string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = PathFor(database), Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
        }
        internal void Cohort(string checks = "[]", string paths = "[]", string detail = "") => Sql("cohort-acceptance.db", """
            CREATE TABLE cohort_receipts(cohort_id TEXT, receipt_id TEXT, completed_at TEXT, failed_checks_json TEXT,
                gate_test_result_paths_json TEXT, infrastructure_detail TEXT, gate_exit_code INTEGER, attributed_members_json TEXT);
            INSERT INTO cohort_receipts VALUES('c1','r1','2026-10-09',$checks,$paths,$detail,1,'[]');
            """, ("$checks", checks), ("$paths", paths), ("$detail", detail));
        internal void Train() => Sql("merge-train-acceptance.db", """
            CREATE TABLE merge_train_receipts(train_id TEXT, receipt_id TEXT, payload_json TEXT);
            INSERT INTO merge_train_receipts VALUES('t1','r1','{"failedChecks":["build"],"gateExitCode":1}');
            """);
        internal void BuildOutput(string outputField = "StdoutPath")
        {
            Write("build.log", "build starting\nC:\\source\\GoalDispatchOperations.cs(483,77): error CS1061: missing member [project.csproj]\nOther.cs(1,1): error CS0001: second compiler error");
            Write("grouped-gate-attempts/a1/a1.attempt.json", JsonSerializer.Serialize(
                new Dictionary<string, string> { [outputField] = PathFor("build.log") }));
        }
        internal async Task<string> Line(string fields)
        {
            var joint = new OwnerJointGateFailureEvidence(Directory);
            var cohort = new OwnerActivityEvidenceReader(PathFor("cohort-acceptance.db"));
            using var scene = new OwnerConsoleActivityOutcomeTests.Scene(evidence: item => joint.Read(item, cohort.Read(item)));
            scene.AddGoals();
            await scene.Render([new(scene.Harness.Clock.GetUtcNow(), "acceptance-cohort", null,
                "ACCEPTANCE_COHORT outcome=failed members=11111111+22222222 " + fields)]);
            return Assert.Single(scene.View.ActivityLines);
        }
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Directory);
    }
}

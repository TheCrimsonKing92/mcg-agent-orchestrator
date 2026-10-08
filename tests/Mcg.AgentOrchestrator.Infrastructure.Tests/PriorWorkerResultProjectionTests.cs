using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PriorWorkerResultProjectionTests
{
    private const string FixtureName = "8257cb24-ba064746-20260924003957.out.txt";
    private const string DeveloperResult = "WORKER_RESULT:\n" +
        "files: src/Feature.cs\ncommands: worker build check\n" +
        "tests: deferred - FeatureTests\ncommit: none\nblockers: none\n" +
        "model_fit: OpenAI/test - adequate - fixture\nskills: none\nconfidence: high\n" +
        "END_WORKER_RESULT";

    [Fact]
    public void ParsedReport_PrecedingProse_PreservesOrderAndStructuredFields()
    {
        const string prose = "Implemented the feature.\n| Fact | Source |\n| F1 | src/Feature.cs:9 |";
        var output = prose + "\n" + DeveloperResult;

        var projection = Project(output);
        var blockOnly = Project(DeveloperResult);

        Assert.Equal(WorkerVerificationEvidence.ContextProjectionValidation.Parsed, projection.Validation);
        Assert.Equal("parsed", projection.ValidationReceiptValue);
        Assert.Equal(ExpectedReceipt(output, "validation=parsed") + Environment.NewLine +
            "Worker report (prose before WORKER_RESULT):" + Environment.NewLine +
            prose + Environment.NewLine + StructuredResult(), projection.Content);
        var blockStart = projection.Content.IndexOf("\nWORKER_RESULT:", StringComparison.Ordinal) + 1;
        Assert.Equal(StructuredResult(), projection.Content[blockStart..]);
        Assert.Equal(ExpectedReceipt(DeveloperResult, "validation=parsed") + Environment.NewLine +
            StructuredResult(), blockOnly.Content);
    }

    [Theory]
    [InlineData(3999)]
    [InlineData(4000)]
    [InlineData(4001)]
    [InlineData(8123)]
    public void ParsedReport_ProseAtCap_KeepsExactTailAndOmissionCount(int proseLength)
    {
        const int cap = 4000;
        const string sentinel = "HEAD_ONLY_SENTINEL";
        var prose = sentinel + new string('p', proseLength - sentinel.Length - 4) + "TAIL";
        var output = prose + "\r\n\r\n" + DeveloperResult;
        var kept = prose.Length > cap ? prose[^cap..] : prose;
        var omission = prose.Length > cap
            ? $"...[{prose.Length - cap} chars omitted from worker report prose; complete source remains at source_handle]..." + Environment.NewLine
            : string.Empty;

        var projection = Project(output);

        Assert.Equal(WorkerVerificationEvidence.ContextProjectionValidation.Parsed, projection.Validation);
        Assert.Equal(ExpectedReceipt(output, "validation=parsed") + Environment.NewLine +
            "Worker report (prose before WORKER_RESULT):" + Environment.NewLine +
            omission + kept + Environment.NewLine + StructuredResult(), projection.Content);
        if (proseLength >= cap + sentinel.Length)
            Assert.DoesNotContain(sentinel, projection.Content, StringComparison.Ordinal);
        Assert.True(kept.Length <= cap);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n\r\n")]
    public void ParsedReport_AbsentProse_KeepsExistingProjection(string prefix)
    {
        var output = prefix + DeveloperResult;

        Assert.Equal(ExpectedReceipt(output, "validation=parsed") + Environment.NewLine +
            StructuredResult(), Project(output).Content);
    }

    [Fact]
    public void MalformedReport_PrecedingAndFollowingProse_KeepsExistingProjection()
    {
        var malformed = DeveloperResult.Replace("skills: none\n", string.Empty, StringComparison.Ordinal);
        var output = "Report before\n\n" + malformed + "\n\nReport after";

        var projection = Project(output);

        Assert.Equal(WorkerVerificationEvidence.ContextProjectionValidation.Malformed, projection.Validation);
        Assert.Equal(ExpectedReceipt(output, "validation=malformed; problem_excerpt=missing field(s): skills.") +
            Environment.NewLine + "Report before" + Environment.NewLine + malformed +
            Environment.NewLine + "Report after", projection.Content);
    }

    [Fact]
    public void NonAuthoritativeReport_PreviewOnly_KeepsExistingProjection()
    {
        var task = new TaskSpec(new TaskId("prior-developer"), "Prior Developer", AgentRole.Developer);
        var verification = new TaskVerificationRecord("test", @"C:\tmp", 0,
            "Preview only", string.Empty, DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            FullStandardOutputUnavailableReason: "legacy-snapshot-authoritative-output-unavailable");
        var expectedContext = string.Join(Environment.NewLine,
            "[legacy verification context]",
            "authoritative: false",
            "unavailable-reason: legacy-snapshot-authoritative-output-unavailable",
            "The complete historical stdout was never retained with an integrity digest. " +
            "The bounded preview below is context only and must not be treated as authoritative evidence.",
            string.Empty,
            "Preview only");

        var projection = WorkerVerificationEvidence.ProjectStandardOutputForContextWithValidation(task, verification);

        Assert.Equal(WorkerVerificationEvidence.ContextProjectionValidation.NonAuthoritative, projection.Validation);
        Assert.Equal(ExpectedReceipt(expectedContext,
            "validation=non-authoritative; problem_excerpt=legacy-snapshot-authoritative-output-unavailable") +
            Environment.NewLine + expectedContext, projection.Content);
    }

    [Fact]
    public void PriorTaskEvidence_DeveloperCitationTable_PreservesAllNineRows()
    {
        // Parallel-safe: all repository and context artifacts belong to this unique root.
        var root = Path.Combine(Path.GetTempPath(), $"prior-task-prose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var git = InfrastructureTestSupport.RunGitProbe(root, ["init"]);
            Assert.True(git.Succeeded, $"git init failed: {git}");
            var kernel = new AgentOrchestratorKernel();
            var developer = new TaskSpec(TaskId.New(), "Developer citation report", AgentRole.Developer);
            var tester = new TaskSpec(TaskId.New(), "Inspect Developer citations", AgentRole.Tester);
            var goal = kernel.CreateGoal("Carry Developer prose into prior task evidence", [developer, tester]);
            var rows = Enumerable.Range(1, 9)
                .Select(index => $"| R{index} | src/Feature{index}.cs:{index * 10} | Verified fact {index} |")
                .ToArray();
            var prose = "Implemented the scoped slice.\n\n| Fact | Source | Evidence |\n| --- | --- | --- |\n" +
                string.Join('\n', rows);
            var stdout = prose + "\n" + DeveloperResult;
            kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
                "codex worker", root, 0, stdout, string.Empty,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                WorkerResultPresent: true, FullStandardOutput: stdout));
            kernel.ReportTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Completed, "Implementation complete.");
            goal = kernel.GetGoal(goal.Id);
            var prior = goal.Tasks.Single(task => task.Id == developer.Id);
            Assert.Equal(WorkTaskStatus.Completed, prior.Status);
            Assert.Equal(stdout, prior.LastVerification!.AuthoritativeStandardOutput);

            var contextDirectory = WorkerContextArtifacts.Write(goal, tester, root, ["profile valid"]);
            var evidence = File.ReadAllText(Path.Combine(contextDirectory, "prior-task-evidence.md"));

            Assert.Contains("## Developer: Developer citation report", evidence, StringComparison.Ordinal);
            Assert.Contains("### Stdout", evidence, StringComparison.Ordinal);
            Assert.Contains("Worker report (prose before WORKER_RESULT):", evidence, StringComparison.Ordinal);
            foreach (var row in rows)
                Assert.Contains(row, evidence, StringComparison.Ordinal);
            Assert.Contains(prose, evidence, StringComparison.Ordinal);
            Assert.Contains(StructuredResult(), evidence, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string StructuredResult() => DeveloperResult.Replace("\n", Environment.NewLine, StringComparison.Ordinal);

    private static string ExpectedReceipt(string output, string validation)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(output);
        return "Artifact receipt: purpose=prior-worker-output; stable_id=prior/prior-developer/verification-output; " +
            $"source_handle=host-captured-authoritative-output; chars={output.Length}; bytes={bytes.Length}; " +
            $"sha256={WorkerContextArtifact.Hash(bytes)}; {validation}";
    }

    [Fact]
    public void HistoricalDeveloperAuditKeepsAllFiftyFourEntries()
    {
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "WorkerOutput", FixtureName));

        var projection = Project(output);

        for (var index = 1; index <= 54; index++)
            Assert.Contains($"audit_{index:00}:", projection.Content, StringComparison.Ordinal);
        Assert.Contains("WORKER_RESULT:", projection.Content, StringComparison.Ordinal);
        Assert.Contains("END_WORKER_RESULT", projection.Content, StringComparison.Ordinal);
        Assert.Equal(WorkerVerificationEvidence.ContextProjectionValidation.Parsed, projection.Validation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OperatorAdjudicationPreservesHistoricalWorkerResultInDownstreamContext(bool passed)
    {
        var root = Path.Combine(Path.GetTempPath(), $"prior-worker-result-{Guid.NewGuid():N}");
        var contextDirectory = Path.Combine(root, "context");
        Directory.CreateDirectory(contextDirectory);
        File.WriteAllText(Path.Combine(contextDirectory, "artifact-registry.json"), "{\"artifacts\":[]}");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var developer = new TaskSpec(TaskId.New(), "Developer audit", AgentRole.Developer);
            var tester = new TaskSpec(TaskId.New(), "Inspect Developer audit", AgentRole.Tester);
            var goal = kernel.CreateGoal("Preserve prior worker output", [developer, tester]);
            var workerOutput = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "WorkerOutput", FixtureName));
            kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
                "codex worker", root, 0, workerOutput, string.Empty,
                DateTimeOffset.Parse("2026-09-24T00:46:30Z"), WorkerResultPresent: true,
                FullStandardOutput: workerOutput));
            const string operatorText = "OPERATOR adjudication: audit reviewed; continue to Tester.";
            kernel.RecordTaskVerification(goal.Id, developer.Id, ManualVerificationRecorder.Create(
                passed, operatorText, root, DateTimeOffset.Parse("2026-09-24T00:49:19Z")));

            var prior = goal.Tasks.Single(task => task.Id == developer.Id);
            Assert.Equal(2, prior.VerificationHistory.Count);
            Assert.Equal(passed ? "manual-verification passed" : "manual-verification failed",
                prior.LastVerification!.Command);
            var brief = new TaskBrief(goal.Id, tester.Id, tester.RequiredRole, tester.Description,
                "# Agent Task Brief\n## Instructions\nInspect the prior Developer result.");
            var package = WorkerProfileDispatcher.BuildContextPackage(
                goal, tester, root, contextDirectory, brief);
            var artifact = Assert.Single(package.Artifacts, candidate =>
                candidate.Identity.Value == $"prior/{developer.Id.Value}/verification-output");
            var projected = artifact.DeliveryMode == ContextDeliveryMode.InlineFull
                ? System.Text.Encoding.UTF8.GetString(artifact.AuthoritativeBytes!)
                : File.ReadAllText(Path.Combine(root,
                    artifact.MandatoryRelativePath!.Replace('/', Path.DirectorySeparatorChar)));

            Assert.Contains("Worker-produced verification", projected, StringComparison.Ordinal);
            for (var index = 1; index <= 54; index++)
                Assert.Contains($"audit_{index:00}:", projected, StringComparison.Ordinal);
            Assert.Contains("Operator adjudication", projected, StringComparison.Ordinal);
            Assert.Contains(operatorText, projected, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MalformedHistoricalAuditRetainsTheCompleteLocatedBlock()
    {
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "WorkerOutput", FixtureName));
        var malformed = output.Replace("skills: verification-before-completion, dotnet-windows-build-hygiene\n",
            string.Empty, StringComparison.Ordinal).Replace(
            "skills: verification-before-completion, dotnet-windows-build-hygiene\r\n",
            string.Empty, StringComparison.Ordinal);

        var projection = Project(malformed);

        Assert.Equal(WorkerVerificationEvidence.ContextProjectionValidation.Malformed, projection.Validation);
        Assert.Contains("missing field(s): skills", projection.Content, StringComparison.Ordinal);
        for (var index = 1; index <= 54; index++)
            Assert.Contains($"audit_{index:00}:", projection.Content, StringComparison.Ordinal);
        Assert.Contains("END_WORKER_RESULT", projection.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedProjectionBoundsProseOutsideTheLocatedBlock()
    {
        var output = new string('p', 8000) + "\nWORKER_RESULT:\n" +
            "files: none\ncommands: none\ntests: deferred - focused receipt needed\n" +
            "blockers: none\naudit_27: middle survives\nEND_WORKER_RESULT\n" +
            new string('s', 8000);

        var projection = Project(output);

        Assert.Equal(WorkerVerificationEvidence.ContextProjectionValidation.Malformed, projection.Validation);
        Assert.Contains("audit_27: middle survives", projection.Content, StringComparison.Ordinal);
        Assert.Contains("chars omitted", projection.Content, StringComparison.Ordinal);
        Assert.True(projection.Content.Length < 5000, $"projected chars={projection.Content.Length}");
    }

    [Fact]
    public void EndMarkerAtEndKeepsBlankLinesInsideTheCompleteBlock()
    {
        const string output = "WORKER_RESULT:\nfiles: none\n\naudit_27: middle survives\nEND_WORKER_RESULT";

        var projection = Project(output);

        Assert.Equal(WorkerVerificationEvidence.ContextProjectionValidation.Malformed, projection.Validation);
        Assert.Contains(output, projection.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void TrailingEchoedOpenerDoesNotHideTheLastCompleteBlock()
    {
        const string output = "WORKER_RESULT:\nfiles: none\naudit_27: middle survives\n" +
            "END_WORKER_RESULT\nThe worker later echoed\nWORKER_RESULT:\nfiles: none";

        var projection = Project(output);

        Assert.Equal(WorkerVerificationEvidence.ContextProjectionValidation.Malformed, projection.Validation);
        Assert.Contains("audit_27: middle survives", projection.Content, StringComparison.Ordinal);
        Assert.Contains("END_WORKER_RESULT", projection.Content, StringComparison.Ordinal);
    }

    private static WorkerVerificationEvidence.ContextProjection Project(string output)
    {
        var task = new TaskSpec(new TaskId("prior-developer"), "Prior Developer", AgentRole.Developer);
        var verification = new TaskVerificationRecord("test", @"C:\tmp", 0,
            output, string.Empty, DateTimeOffset.UtcNow, FullStandardOutput: output);
        return WorkerVerificationEvidence.ProjectStandardOutputForContextWithValidation(task, verification);
    }
}

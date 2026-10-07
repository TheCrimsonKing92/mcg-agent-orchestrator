using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class ConductorStewardModelRoundBundleTestsEvidenceForms
{
    [Theory]
    [InlineData("PlannerOutputContractRejected")]
    [InlineData("DeveloperGateReopenNoCommit")]
    [InlineData("DeveloperReviewerFindingNoCommit")]
    public async Task Prompt_names_evidence_forms_and_places_explanations_in_text(string triggerKind)
    {
        using var temp = new TempDirectory();
        WorkerProcessRunRequest? request = null;
        var round = new ClaudeConductorStewardModelRound(System.IO.Path.Combine(temp.Path, "receipts"),
            (value, _) =>
            {
                request = value;
                return Task.FromResult(new WorkerProcessRunResult(0, "adjudication", ""));
            }, new NoFiles(), (_, _) => false);
        var trigger = new ConductorStewardTrigger("goal", "task", "candidate-sha",
            Enum.Parse<ConductorStewardTriggerKind>(triggerKind),
            new DateTimeOffset(2026, 10, 7, 15, 0, 0, TimeSpan.Zero), "Planner contract rejected", "", [], ["criterion"]);

        await round.DispatchAsync(trigger, temp.Path, CancellationToken.None);

        Assert.NotNull(request);
        var prompt = request!.StandardInput!;
        Assert.Contains("trx:<repository path>", prompt);
        Assert.Contains("operator-evidence:<repository path>", prompt);
        Assert.Contains("focused-evidence:<pointer>", prompt);
        Assert.Contains("acceptance-attempt:<attempt id>", prompt);
        Assert.Contains("name=value", prompt);
        Assert.Contains("explanations belong in text, not in evidenceReferences.", prompt);
        const string fields = "An ask-owner has kind, question, and evidenceReferences. A no-action has kind and reason.";
        Assert.Contains(fields, prompt);
        var afterFields = prompt[(prompt.IndexOf(fields, StringComparison.Ordinal) + fields.Length)..];
        Assert.StartsWith("Each evidenceReferences entry must be trx:<repository path>", afterFields.TrimStart());
    }

    private sealed class NoFiles : IConductorStewardTrackedFileLister
    {
        public IReadOnlyList<string> MatchingFiles(string worktree, string stem) => [];
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcg-steward-evidence-forms", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        internal string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

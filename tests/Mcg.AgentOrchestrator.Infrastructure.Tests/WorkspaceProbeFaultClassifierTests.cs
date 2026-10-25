using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class WorkspaceProbeFaultClassifierTests
{
    internal const string F1 = """Goal workspaces require 'C:\repo\project' to be inside a git work tree. probe='git rev-parse --is-inside-work-tree'; exit=-1; processStarted=True; drainTimedOut=False; stdoutBytes=0; stdout=''; stderr='git rev-parse --is-inside-work-tree timed out after 60000ms'; directoryExists=True""";
    internal static string F2 => "background-acceptance-fault: " + F1.Replace(' ', '_');
    internal static string F3 => F1.Replace("exit=-1", "exit=128", StringComparison.Ordinal)
        .Replace("stderr='git rev-parse --is-inside-work-tree timed out after 60000ms'",
            "stderr='fatal: not a git repository (or any of the parent directories)'", StringComparison.Ordinal);
    internal static string F4 => F1.Replace("directoryExists=True", "directoryExists=False", StringComparison.Ordinal);
    internal static string F5 => F1.Replace("processStarted=True", "processStarted=False", StringComparison.Ordinal)
        .Replace("stderr='git rev-parse --is-inside-work-tree timed out after 60000ms'", "stderr=''", StringComparison.Ordinal);
    internal static string F6 => F1[..F1.IndexOf("; directoryExists=", StringComparison.Ordinal)];

    public static TheoryData<string?, string> Diagnostics => new()
    {
        { F1, "HostContentionTimeout" },
        { F2, "HostContentionTimeout" },
        { F3, "Definitive" },
        { F4, "Definitive" },
        { F5, "Definitive" },
        { F6, "Definitive" },
        { "worker-process-start-failed: pid=1; stage=owned-process-group-launch", "None" },
        { "Verification command timed out after elapsed=25m budget=25m.", "None" },
        { null, "None" }
    };

    [Theory]
    [MemberData(nameof(Diagnostics))]
    public void ClassifiesOnlyCompleteTimeoutEvidence(string? detail, string expected) =>
        Assert.Equal(expected, WorkspaceProbeFaultClassifier.Classify(detail).ToString());

    [Theory]
    [InlineData("; exit=-1")]
    [InlineData("; processStarted=True")]
    [InlineData("; drainTimedOut=False")]
    [InlineData("; stdoutBytes=0")]
    [InlineData("; stdout=''")]
    [InlineData("; stderr='git rev-parse --is-inside-work-tree timed out after 60000ms'")]
    [InlineData("; directoryExists=True")]
    public void MissingDiagnosticFieldIsDefinitive(string field) =>
        Assert.Equal(WorkspaceProbeFaultKind.Definitive,
            WorkspaceProbeFaultClassifier.Classify(F1.Replace(field, "", StringComparison.Ordinal)));

    [Theory]
    [InlineData("exit=-1", "exit=-10")]
    [InlineData("stdoutBytes=0", "stdoutBytes=01")]
    [InlineData("processStarted=True", "processStarted=true")]
    [InlineData("drainTimedOut=False", "drainTimedOut=unknown")]
    [InlineData("stdout=''", "stdout=")]
    [InlineData("directoryExists=True", "directoryExists=True; directoryExists=False")]
    public void MalformedOrConflictingDiagnosticIsDefinitive(string before, string after) =>
        Assert.Equal(WorkspaceProbeFaultKind.Definitive,
            WorkspaceProbeFaultClassifier.Classify(F1.Replace(before, after, StringComparison.Ordinal)));

    [Theory]
    [InlineData("drainTimedOut=False", "drainTimedOut=True")]
    [InlineData("stdout=''", "stdout='probe; note=ignored'")]
    [InlineData("stderr='git rev-parse --is-inside-work-tree timed out after 60000ms'", "stderr='timed out after 60000ms; diagnostic detail'")]
    public void CompleteTimeoutPreservesQuotedOutputAndUnrestrictedFields(string before, string after) =>
        Assert.Equal(WorkspaceProbeFaultKind.HostContentionTimeout,
            WorkspaceProbeFaultClassifier.Classify(F1.Replace(before, after, StringComparison.Ordinal)));

    [Fact]
    public void FirstFailureBacksOffForTwoMinutes() =>
        Assert.Equal(TimeSpan.FromSeconds(120), WorkspaceProbeFaultClassifier.BackoffAfter(1));

    [Fact]
    public void SecondFailureBacksOffForFiveMinutes() =>
        Assert.Equal(TimeSpan.FromSeconds(300), WorkspaceProbeFaultClassifier.BackoffAfter(2));

    [Theory]
    [InlineData(1, 120)]
    [InlineData(2, 300)]
    public void DeadlineAddsBackoffToCompletion(int count, int seconds)
    {
        var completion = new DateTimeOffset(2026, 10, 25, 9, 20, 44, TimeSpan.Zero);
        Assert.Equal(completion.AddSeconds(seconds), WorkspaceProbeFaultClassifier.NotBefore(completion, count));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void UnsupportedCountFailsLoudly(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkspaceProbeFaultClassifier.BackoffAfter(count));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkspaceProbeFaultClassifier.NotBefore(default, count));
    }
}

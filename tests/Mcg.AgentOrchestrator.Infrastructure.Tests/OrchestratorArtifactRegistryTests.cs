using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;
using static Mcg.AgentOrchestrator.App.Orchestration.OrchestratorArtifactRegistry;

// Parallel safe: each fixture owns a private temporary root; repository source is read-only.
public sealed class OrchestratorArtifactRegistryTests
{
    [Fact]
    public void FindUnregistered_StraysAndControls_ReportsOnlyUnknownNames()
    {
        var root = CreateTempDirectory();
        try
        {
            var directory = Path.Combine(root, ".orchestrator");
            Directory.CreateDirectory(Path.Combine(directory, "logs"));
            Directory.CreateDirectory(Path.Combine(directory, "acceptance-gate-attempts"));
            foreach (var name in new[] { "state.db", "state.db-wal", "STATE.DB-shm",
                "agents.json.bak-20261016-x", "run-events.db.maintenance.lease", "stray.json" })
                File.WriteAllText(Path.Combine(directory, name), "");
            Directory.CreateDirectory(Path.Combine(directory, "p289-short-control"));
            Assert.Equal(new[] { "p289-short-control", "stray.json" }, FindUnregistered(directory));

            Directory.Delete(Path.Combine(directory, "p289-short-control"));
            File.Delete(Path.Combine(directory, "stray.json"));
            Assert.Empty(FindUnregistered(directory));
            Assert.Empty(FindUnregistered(Path.Combine(root, "missing")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Registry_WriterEvidenceAndMetadata_HasNoDeadClasses()
    {
        var invalid = FindInvalid(VerifiedRepositoryRoot.Find(), Entries);
        Assert.True(invalid.Count == 0, "Invalid artifact classes: " + string.Join(", ", invalid));
    }

    [Fact]
    public void WriterGuard_MissingFileOrLiteral_ReportsExactlyThosePatterns()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "writer.cs"), "valid");
            Entry[] entries = [
                new("wrong-literal", "writer", WriterKind.Source, "writer.cs", RetentionKind.Unmanaged, null, 1, ""),
                new("missing-file", "writer", WriterKind.Source, "missing.cs", RetentionKind.Unmanaged, null, 1, ""),
                new("valid", "writer", WriterKind.Source, "writer.cs", RetentionKind.Unmanaged, null, 1, ""),
                new("manual", "operator", WriterKind.Operator, null, RetentionKind.Unmanaged, null, 1, "Operator owned.")
            ];
            Assert.Equal(new[] { "missing-file", "wrong-literal" }, FindInvalid(root, entries));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Registry_Policies_ReferenceExistingRetentionAges()
    {
        foreach (var name in new[] { "acceptance-gate-attempts", "pre-review-evidence-attempts" })
        {
            var entry = Assert.IsType<Entry>(Classify(name));
            Assert.Equal(RetentionKind.ReclaimAfterGoalTerminalPlusDays, entry.Retention);
            Assert.Equal(StorageRetentionMaintenance.AcceptanceArtifactMaxAge, entry.RetentionAge);
        }
        var prompts = Assert.IsType<Entry>(Classify("prompts"));
        Assert.Equal(RetentionKind.ReclaimAfterGoalTerminalPlusDays, prompts.Retention);
        Assert.Equal(StorageRetentionMaintenance.WorkerDeletionAge, prompts.RetentionAge);
        var logs = Assert.IsType<Entry>(Classify("logs"));
        Assert.Equal(RetentionKind.KeepDays, logs.Retention);
        Assert.Equal(StorageRetentionMaintenance.WorkerDeletionAge, logs.RetentionAge);
        Assert.Equal(RetentionKind.KeepForever, Assert.IsType<Entry>(Classify("goal-events")).Retention);
        Assert.Null(Classify("lane-reuse-shadow"));
    }

    [Fact]
    public void Classify_ExactWildcardAndSidecars_UsesDeclaredPrecedence()
    {
        Entry[] entries = [
            new("*.bak*", "operator", WriterKind.Operator, null, RetentionKind.Unmanaged, null, 1, "Manual copies."),
            new("state.db", "writer", WriterKind.Source, "writer.cs", RetentionKind.KeepForever, null, 1, ""),
            new("state.db.bak", "operator", WriterKind.Operator, null, RetentionKind.Unmanaged, null, 1, "Exact copy."),
            new("*.db*", "operator", WriterKind.Operator, null, RetentionKind.Unmanaged, null, 1, "Later wildcard.")
        ];
        Assert.Same(entries[2], Classify("STATE.DB.BAK", entries));
        Assert.Same(entries[0], Classify("x.BAK", entries));
        Assert.Same(entries[0], Classify("x.bak-tail", entries));
        Assert.Same(entries[3], Classify("STATE.DB-shm", entries));
        Assert.Same(entries[1], Classify("STATE.DB-WAL", entries[..3]));
        Assert.Same(entries[1], Classify("state.db-shm", entries[..3]));
        Assert.Null(Classify("not-a-store", entries));
        Assert.Null(Classify("xXbak", entries));
        Assert.Equal(".bak", entries[0].WriterLiteral);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-artifact-registry-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static IReadOnlyList<string> FindInvalid(string root, IReadOnlyList<Entry> entries)
    {
        var duplicates = entries.GroupBy(e => e.Pattern, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return entries.Where(entry =>
        {
            if (duplicates.Contains(entry.Pattern) || entry.BudgetBytes <= 0) return true;
            if (entry.Writer != WriterKind.Source)
                return entry.WriterPath is not null || string.IsNullOrWhiteSpace(entry.Note);
            if (entry.WriterPath is null) return true;
            var path = Path.Combine(root, entry.WriterPath);
            return !File.Exists(path) || !File.ReadAllText(path).Contains(entry.WriterLiteral, StringComparison.Ordinal);
        }).Select(entry => entry.Pattern).Order(StringComparer.Ordinal).ToArray();
    }
}

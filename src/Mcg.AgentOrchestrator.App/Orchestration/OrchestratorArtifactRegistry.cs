namespace Mcg.AgentOrchestrator.App.Orchestration;

// Top-level storage classes describe custody and current policy; budgets are advisory.
internal static class OrchestratorArtifactRegistry
{
    internal enum WriterKind { Source, Operator, Retired }
    internal enum RetentionKind { KeepForever, KeepDays, ReclaimAfterGoalTerminalPlusDays, Unmanaged }

    internal sealed record Entry(
        string Pattern, string Owner, WriterKind Writer, string? WriterPath,
        RetentionKind Retention, TimeSpan? RetentionAge, long BudgetBytes, string Note,
        string? WriterLiteral = null)
    {
        public string WriterLiteral { get; init; } = WriterLiteral ?? Pattern.Replace("*", "");
    }

    private const long MiB = 1024L * 1024;
    private const long GiB = 1024 * MiB;

    internal static IReadOnlyList<Entry> Entries { get; } = Array.AsReadOnly<Entry>([
        Source("logs", "App/Orchestration/OrchestratorWorkspace.cs",
            note: "operator-*.log files are swept at WorkerDeletionAge; conductor-tick-stall *.dmp dumps keep the newest 2 and older dumps past 3 days are reclaimed by ConductorDiagnosticDumpRetention; spec-refinement, post-landing-canary, conduct-supervisor, supervisor-handoff and supervisor-ready runs past 14 days or past the newest 500 per family are reclaimed by LogDirectoryFamilyRetention, keeping the newest 20; dispatch artifacts follow the worker artifact policy; other files are not swept."),
        Source("prompts", "App/Orchestration/OrchestratorWorkspace.cs"),
        Source("goal-events", "App/Orchestration/OrchestratorWorkspace.cs"),
        Source("spec-refinement-launch-attempts", "App/Orchestration/OrchestratorWorkspace.cs"),
        Source("projects", "App/Orchestration/OrchestratorWorkspace.cs"),
        Source("state.db", "App/Orchestration/OrchestratorWorkspace.cs"),
        Source("run-events.db", "App/Orchestration/OrchestratorWorkspace.cs"),
        Source("backlog.db", "App/Orchestration/OrchestratorWorkspace.cs"),
        Source("dogfood-log.db", "App/Orchestration/OrchestratorWorkspace.cs"),
        Source("experiments.db", "App/Orchestration/OrchestratorWorkspace.cs"),
        Source("portfolio.db", "App/Orchestration/OrchestratorWorkspace.cs"),
        Source("semantic-acceptance.jsonl", "App/Orchestration/OrchestratorWorkspace.cs"),
        Source("agents.json", "App/Orchestration/OrchestratorWorkspace.cs"),
        Source("model-functions.json", "App/Orchestration/OrchestratorWorkspace.cs"),
        Source("operator-channel.json", "App/Orchestration/OrchestratorWorkspace.cs"),
        Source("workers.json", "App/Orchestration/OrchestratorWorkspace.cs"),
        Source("spec-refiner-precedents.json", "App/Orchestration/OrchestratorWorkspace.cs"),
        Source("acceptance-gate-attempts", "Infrastructure/Workspaces/GoalAcceptanceVerifier.cs"),
        Source("pre-review-evidence-attempts", "App/Orchestration/OwnedChildAttemptCoordinatorFactory.cs"),
        Source("grouped-gate-attempts", "App/Orchestration/OwnedChildAttemptCoordinatorFactory.cs"),
        Source("merge-train-evidence", "Execution/Persistence/MergeTrainAcceptanceStore.cs",
            note: "TRX copies of train receipts that cannot back a landing are reclaimed by MergeTrainEvidenceRetention 14 days after the receipt completes, and those of finalized landings by LandedMergeTrainEvidenceRetention 3 days after, once every member goal is Completed, Cancelled or Superseded; unlanded passing trains, prepared landings and gate-verdict.json are kept."),
        Source("cohort-acceptance.db.artifacts", "Execution/Persistence/CohortAcceptanceStore.cs", literal: ".artifacts",
            note: "TRX copies of cohort receipts that cannot back a landing are reclaimed by CohortAcceptanceEvidenceRetention 14 days after the receipt completes once every member goal is Completed, Cancelled or Superseded; landing-evidence receipts, judge-panel trigger receipts and gate-verdict.json are kept."),
        Source("goal-operations", "App/Orchestration/GoalOperationJournal.cs"),
        Source("author-drafts", "App/Orchestration/AuthorBriefDraftService.cs"),
        Source("author-rounds", "App/Orchestration/ConductorAuthorHost.cs"),
        Source("author-claims.db", "App/Orchestration/ConductorAuthorHost.cs"),
        Source("board-fill-drafts", "App/Orchestration/ConductorBoardFillHost.Default.cs"),
        Source("board-fill.db", "App/Orchestration/ConductorBoardFillDraftStore.cs"),
        Source("steward-rounds", "App/Orchestration/ConductorStewardHost.cs"),
        Source("steward-triggers.db", "App/Orchestration/ConductorStewardHost.cs"),
        Source("backups", "App/Orchestration/ConductorHandoffSchemaUpgrade.cs"),
        Source("continuity", "App/Program.cs"),
        Source("handoff", "App/Orchestration/ConductorLoopHandoff.cs"),
        Source("conduct-loop.lock", "App/Orchestration/ConductorLoopHandoff.cs"),
        Source("remote-executor-occupancy", "Infrastructure/Workspaces/RemoteExecutorOccupancy.cs"),
        Source("test-reuse-shadow", "Infrastructure/Workspaces/AcceptanceTestReuseShadow.cs"),
        Source("cohort-acceptance.db", "App/Orchestration/ConductorDriver.cs"),
        Source("merge-train-acceptance.db", "App/Orchestration/ConductorDriver.cs"),
        Source("follower-gate-acceptance.db", "App/Orchestration/ConductorDriver.FollowerGates.cs"),
        Source("judge-panel.db", "App/Orchestration/ConductorJudgePanelHost.Default.cs"),
        Source("collaboration-items.db", "Execution/Persistence/CollaborationItemStore.Storage.cs"),
        Source("operator-directives.db", "Execution/Persistence/SqliteOperatorDirectiveStore.cs"),
        Source("operator-escapes.db", "Execution/Persistence/OperatorEscapeStore.cs", owner: "SqliteOperatorEscapeStore"),
        Source("operator-intents.db", "Execution/Persistence/OperatorIntentStore.cs", owner: "SqliteOperatorIntentStore"),
        Source("operator-lessons.db", "Execution/Persistence/OperatorLessonStore.cs", owner: "SqliteOperatorLessonStore"),
        Source("progressive-review-glance-circuit.db", "Execution/Persistence/ProgressiveReviewGlanceCircuitStore.cs", owner: "SqliteProgressiveReviewGlanceCircuitStore"),
        Source("progressive-review-steering.db", "Execution/Persistence/ProgressiveReviewSteeringStore.cs", owner: "SqliteProgressiveReviewSteeringStore"),
        Source("trx-coherence-verdicts.jsonl", "Core/Domain/TrxCoherenceVerdictStore.cs"),
        Source("acceptance-closure-verdicts.jsonl", "Infrastructure/Workspaces/AcceptanceClosureVerdictIndex.cs"),
        Source("acceptance-lane-durations.jsonl", "Infrastructure/Workspaces/AcceptanceLaneDurationStore.cs"),
        Source("gate-lane-time.jsonl", "Infrastructure/Workspaces/GateLaneTimeLedger.cs"),
        Source("gate-lane-receipts.jsonl", "Infrastructure/Workspaces/GateLaneReceiptLedger.cs"),
        Source("gate-lane-test-baselines.json", "Infrastructure/Workspaces/GateLaneBaselineStore.cs"),
        Source("gate-lane-slowdowns.jsonl", "Infrastructure/Workspaces/GateLaneSlowdownDiff.cs"),
        Source("host-health.jsonl", "Infrastructure/Workspaces/GateHostHealthLedger.cs"),
        Source("lane-flakes.jsonl", "Infrastructure/Workspaces/AcceptanceLaneFlakeLedger.cs"),
        Source("remote-executor-health.jsonl", "Infrastructure/Workspaces/RemoteExecutorHealthLedger.cs"),
        Source("remote-executor-probe.jsonl", "Infrastructure/Workspaces/RemoteExecutorProbeLedger.cs"),
        Source("executor-tools-deploy", "App/Orchestration/ExecutorToolsMainSource.cs"),
        Source("tick-cpu.jsonl", "Infrastructure/Workspaces/TickCpuLedger.cs"),
        Source("remote-lane-executors.json", "Infrastructure/Workspaces/RemoteLaneExecutorConfiguration.cs"),
        Source("prompt-rollout-watches.jsonl", "App/Orchestration/PromptRolloutWatchStore.cs"),
        Source("conductor-policy.json", "App/Orchestration/GoalRefinementWorkCoordinator.Policy.cs"),
        Source("failure-clusters-daily-state.json", "App/Orchestration/FailureClustersDailyRunner.cs"),
        Source("host-health-state.json", "App/Orchestration/ConductorHostHealthMonitor.cs"),
        Source("program-health-state.json", "App/Orchestration/ConductorProgramHealthMonitor.cs"),
        Source("state-log-divergence-state.json", "App/Orchestration/StateLogDivergenceCheckRunner.cs"),
        Source("terminal-goal-sweep-cache.json", "App/Orchestration/TerminalGoalSweep.cs"),
        Source("storage-retention-cursor.json", "App/Orchestration/StorageRetentionResumeCursor.cs"),
        Source("landing-escalations.json", "App/Orchestration/OperatorInbox.cs"),
        Source("landing-escalations.lock", "App/Orchestration/OperatorInbox.cs"),
        Source("operator-inbox-acks.json", "App/Orchestration/OperatorInbox.cs"),
        Source("ownership-holds.json", "App/Orchestration/OperatorInbox.cs"),
        Source("discord-collaboration-refs.json", "Infrastructure.OperatorComms/DiscordCollaborationViewService.cs"),
        Source("*.maintenance.lease", "Execution/Persistence/SqliteMaintenanceLease.cs"),
        Source("*.post-landing-canary.lock", "App/Orchestration/PostLandingCanaryCoordinator.cs"),
        new Entry("firewall-rule-removal", "Remove-TestSlotFirewallRules", WriterKind.Source,
            "scripts/Remove-TestSlotFirewallRules.ps1", RetentionKind.Unmanaged, null, 256 * MiB, "Operator script output."),
        new Entry("last-drive.json", "Start-OrchestratorCommand", WriterKind.Source,
            "scripts/Start-OrchestratorCommand.ps1", RetentionKind.KeepForever, null, 256 * MiB, "Last operator launch configuration."),
        Source("rollback", "App/Orchestration/GoalRollbackPlanner.cs",
            note: "Written only by the CLI accept fast-forward path; last write 2026-09-12; 215 per-goal JSON files."),
        Operator("operator-evidence", RetentionKind.KeepForever),
        Operator("operator-tools", RetentionKind.KeepForever),
        Operator("claude-operator-config", RetentionKind.KeepForever),
        Operator("traces"),
        Operator("worktree-dirty-backups"),
        Operator("diagnostics"),
        Operator("*.bak*"),
        Retired("events", "259 files; last write 2026-07-08."),
        Retired("config-switch-backups", "Last write 2026-08-25."),
        Retired("operator-receipts", "Last write 2026-08-31."),
        Retired("tools", "Last write 2026-07-26."),
        Retired("isolated-artifact-sweep.json", "Last write 2026-08-31."),
        Retired("operator-decisions.json", "Last write 2026-06-17."),
        Retired("state.json", "Last write 2026-06-11.")
    ]);

    internal static Entry? Classify(string topLevelName, IReadOnlyList<Entry>? entries = null)
    {
        entries ??= Entries;
        var exact = entries.FirstOrDefault(e => e.Pattern.Equals(topLevelName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var wildcard = entries.FirstOrDefault(e => e.Pattern.Contains('*') && Matches(topLevelName, e.Pattern));
        if (wildcard is not null) return wildcard;
        return topLevelName.EndsWith("-shm", StringComparison.OrdinalIgnoreCase) ||
            topLevelName.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
            ? Classify(topLevelName[..^4], entries) : null;
    }

    internal static IReadOnlyList<string> FindUnregistered(string orchestratorDirectory, IReadOnlyList<Entry>? entries = null) =>
        !Directory.Exists(orchestratorDirectory) ? [] : Directory.EnumerateFileSystemEntries(orchestratorDirectory)
            .Select(Path.GetFileName).OfType<string>().Where(name => Classify(name, entries) is null)
            .Order(StringComparer.Ordinal).ToArray();

    private static bool Matches(string name, string pattern)
    {
        // '*' is the only metacharacter; retry after the last star on a mismatch.
        var at = 0;
        var part = 0;
        var star = -1;
        var retry = 0;
        while (at < name.Length)
        {
            if (part < pattern.Length && pattern[part] == '*') { star = part++; retry = at; }
            else if (part < pattern.Length && name.AsSpan(at, 1).Equals(pattern.AsSpan(part, 1), StringComparison.OrdinalIgnoreCase))
            { at++; part++; }
            else if (star >= 0) { part = star + 1; at = ++retry; }
            else return false;
        }
        while (part < pattern.Length && pattern[part] == '*') part++;
        return part == pattern.Length;
    }

    private static Entry Source(string pattern, string path, string? owner = null, string? literal = null, string? note = null)
    {
        var retention = pattern switch
        {
            "acceptance-gate-attempts" or "pre-review-evidence-attempts" or "prompts" or "merge-train-evidence" or "cohort-acceptance.db.artifacts" => RetentionKind.ReclaimAfterGoalTerminalPlusDays,
            "logs" => RetentionKind.KeepDays,
            "goal-events" or "goal-operations" => RetentionKind.KeepForever,
            _ when pattern.EndsWith(".db", StringComparison.Ordinal) || pattern.EndsWith(".json", StringComparison.Ordinal) => RetentionKind.KeepForever,
            _ => RetentionKind.Unmanaged
        };
        TimeSpan? age = pattern switch
        {
            "acceptance-gate-attempts" or "pre-review-evidence-attempts" or "merge-train-evidence" or "cohort-acceptance.db.artifacts" => StorageRetentionMaintenance.AcceptanceArtifactMaxAge,
            "logs" or "prompts" => StorageRetentionMaintenance.WorkerDeletionAge,
            _ => null
        };
        return new(pattern, owner ?? Path.GetFileName(path).Split('.')[0], WriterKind.Source,
            "src/Mcg.AgentOrchestrator." + path, retention, age, Budget(pattern),
            note ?? (retention == RetentionKind.Unmanaged ? "No sweep covers this class." : "Existing retention policy; budget is advisory."), literal);
    }

    private static Entry Operator(string pattern, RetentionKind retention = RetentionKind.Unmanaged) =>
        new(pattern, "operator", WriterKind.Operator, null, retention, null, Budget(pattern), "Operator-managed artifacts; no repository writer.");

    private static Entry Retired(string pattern, string note) =>
        new(pattern, "none", WriterKind.Retired, null, RetentionKind.Unmanaged, null, Budget(pattern), note);

    private static long Budget(string pattern) => pattern switch
    {
        "acceptance-gate-attempts" => 12 * GiB,
        "logs" or "merge-train-evidence" or "cohort-acceptance.db.artifacts" or "operator-evidence" or "state.db" => 2 * GiB,
        "pre-review-evidence-attempts" or "prompts" or "goal-events" or "goal-operations" => GiB,
        "run-events.db" => 512 * MiB,
        _ => 256 * MiB
    };
}

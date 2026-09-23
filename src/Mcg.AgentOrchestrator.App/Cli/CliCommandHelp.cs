using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliCommandHelp
{
    public const string ConductUsage = "Usage: conduct <goal-id-prefix> [--policy <Conservative|Permissive|Manual>] [--watch [--poll-seconds <n>]], or conduct --loop [--max-iterations <n>] [--max-duration <seconds>] [--watch|--daemon] [--poll-seconds <n>] [--unscoped-stall-ticks <n>]";
    public const string GoalUsage = "Usage: goal <objective> [--pipeline <auto|five-role|developer-reviewer|developer-only>] [--simple] [--from-backlog] [--run --confirm-batch-start] [--backlog-item <id-prefix> --backlog-coverage <full|slice>] [--request-key <key>] | goal --text-file <path> [--pipeline <auto|five-role|developer-reviewer|developer-only>] [--request-key <key>] | goal --brief-file <path> [--pipeline <auto|five-role|developer-reviewer|developer-only>] [--request-key <key>]";
    public const string SimpleGoalUsage = "Usage: simple-goal <objective> [--dispatch --confirm-dispatch-start] [--backlog-item <id-prefix> --backlog-coverage <full|slice>] [--request-key <key>] | simple-goal --text-file <path> | simple-goal --brief-file <path>";
    public const string BacklogIntakeUsage = "Usage: backlog-intake [filter...] [--create-goal|--create-simple-goal] [--force-reclaim] [--pipeline <auto|five-role|developer-reviewer|developer-only>] [--backlog-item <id-prefix> --backlog-coverage <full|slice>] [--request-key <key>]";
    public const string AcceptanceUsage = "Usage: acceptance [goal-id-prefix] [--skip-verify] [--keep-workspace] [--no-record] [--autonomy <policy>]";
    public const string RunGoalUsage = "Usage: run-goal [goal-id-prefix] --confirm-batch-start [--confirm-large-paid-subscription-start] [--confirm-readiness-risk] [--autonomy <policy>]";
    public const string GoalIntakeStatusUsage = "Usage: goal-intake-status <request-key>";
    public const string GoalBoardUsage = GoalBoardOptions.Usage;
    public const string GoalReplaceUsage = "Usage: goal-replace <predecessor-goal-id> --brief-file <path> --reason-file <path> --request-id <guid> --disposition <zero-work-correction|abandon-failed-attempt|supersede-unlanded-attempt> --confirm-goal-replace [--pipeline <auto|five-role|developer-reviewer|developer-only>] [--ideation <agent>|--researcher <agent>|--planner <agent>|--developer <agent>|--tester <agent>|--reviewer <agent>]";
    public const string AddTaskUsage = "Usage: add-task [--goal <goal-prefix>] <role> <description> [--before-role <role>] | add-task [--goal <goal-prefix>] <role> --text-file <path> [--before-role <role>]";
    public const string RetryUsage = "Usage: retry [--goal <goal-prefix>] <task-number> <message> [--cause <cause>] [--goal <goal-prefix>] [--mechanical] | retry [--goal <goal-prefix>] <task-number> --text-file <path> [--cause <cause>] [--goal <goal-prefix>] [--mechanical]";
    public const string NoteUsage = "Usage: note <task-number> <message> [--gate-deliverable <id>...] | note <goal-prefix> <task-number> <message> [--gate-deliverable <id>...] | note --goal <goal-prefix> <task-number> <message> [--gate-deliverable <id>...] | note <task-number> --text-file <path> [--gate-deliverable <id>...]";
    public const string ProgressUsage = "Usage: progress [--goal <goal-prefix>] <task-number> <status> <message> [--goal <goal-prefix>] | progress [--goal <goal-prefix>] <task-number> <status> --text-file <path> [--goal <goal-prefix>]";
    public const string VerifyManualUsage = "Usage: verify-manual [--goal <goal-prefix>] <task-number> <passed|failed> <note> [--goal <goal-prefix>] | verify-manual [--goal <goal-prefix>] <task-number> <passed|failed> --text-file <path> [--goal <goal-prefix>]";
    public const string AdjudicateUsage = "Usage: adjudicate [--goal <goal-prefix>] <task-number> <close|reopen-regate|route> --text-file <path> --evidence <ref>... [--cause <cause>] [--actor-kind <human|agent>]";
    public const string RecoverUsage = "Usage: recover <goal-prefix> <note> | recover <goal-prefix> --text-file <path>";
    public const string AcceptanceRetryUsage = "Usage: acceptance-retry <goal-prefix> <reason> --confirm-acceptance-retry";
    public const string GoalAmendUsage = "Usage: goal-amend <goal-prefix> --waive <criterion-number|exact-text> --reason <reason> [--disposition <criterion-reference>=<prose>] [--disposition-file <criterion-reference>=<path>] [--actor <name>] | goal-amend <goal-prefix> --waive <criterion-number|exact-text> --reason-file <path> [--disposition <criterion-reference>=<prose>] [--disposition-file <criterion-reference>=<path>] [--actor <name>]";
    public const string ReviseUsage = "Usage: revise <goal-prefix> <new-brief> [--reason <reason>] [--supersede-answer <clarification-id>=<replacement>...] | revise <goal-prefix> --brief-file <path|-> [--reason <reason>|--reason-file <path|->] [--supersede-answer <clarification-id>=<replacement>...] | revise <goal-prefix> --history (- reads stdin for revise file flags)";
    public const string AnswerUsage = "Usage: answer <request-id> <answer> [--gate-deliverable <id>...] | answer <request-id> --text-file <path> [--gate-deliverable <id>...]";
    public const string SupersedeUsage = "Usage: supersede <goal-id> <clarification-id> <answer> | supersede <goal-id> <clarification-id> --text-file <path>";
    public const string GateSatisfiedUsage = "Usage: gate-satisfied <request-id|task-note-record-id> <deliverable-id> <evidence> | gate-satisfied <request-id|task-note-record-id> <deliverable-id> --text-file <path>";
    public const string AttentionUsage = "Usage: attention show [--all|--include-parked] [--goal] <goal-id-prefix> | attention dismiss --item <item-id> | attention dismiss [--goal] <goal-id-prefix> | attention answer [<goal-id-prefix>] <clarification-id|human-wait-request-id> <answer> | attention answer [<goal-id-prefix>] <clarification-id|human-wait-request-id> --text-file <path>";
    public const string AbandonGoalUsage = "Usage: abandon-goal <goal-id-prefix> <reason> [--confirm-goal-abandon] | abandon-goal <goal-id-prefix> --text-file <path> [--confirm-goal-abandon]";
    public const string CancelGoalUsage = "Usage: cancel-goal <goal-id-prefix> <reason> [--confirm-goal-stop] | cancel-goal <goal-id-prefix> --text-file <path> [--confirm-goal-stop]";
    public const string SupersedeGoalUsage = "Usage: supersede-goal <goal-id-prefix> <reason> [--confirm-goal-stop] | supersede-goal <goal-id-prefix> --text-file <path> [--confirm-goal-stop]";
    public const string ParkGoalUsage = "Usage: park-goal <goal-id-prefix> <reason> [--confirm-goal-park] | park-goal <goal-id-prefix> --text-file <path> [--confirm-goal-park]";
    public const string UnparkGoalUsage = "Usage: unpark-goal <goal-id-prefix> <reason> [--confirm-goal-unpark] | unpark-goal <goal-id-prefix> --text-file <path> [--confirm-goal-unpark]";
    public const string StopUsage = "Usage: stop <goal-id-prefix> <reason> --as cancel|park|abandon|supersede | stop <goal-id-prefix> --text-file <path> --as cancel|park|abandon|supersede";
    public const string SubscriptionDispatchUsage = "Usage: subscription-dispatch <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> [--confirm-limit-review <note>|--confirm-limit-review --text-file <path>] [--subscription-model <model>] [--subscription <profile>] [--subscription-reasoning <effort>] [--allow-git-reference]";
    public const string RefreshDispatchSyntax = "refresh-dispatch <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> [--history] [--history-limit <n>]";
    public const string RefreshDispatchUsage = "Usage: " + RefreshDispatchSyntax;
    public const string InquiryUsage = "Usage: inquiry <goal-prefix> <task-number> --text-file <question>";
    public const string AgentUsage = "Usage: agent <role> <provider> <model> [name] [--complex-model <model>] [--subscription-model <model>] [--subscription-reasoning <effort>]";
    public const string AgentAddUsage = "Usage: agent-add <role> <provider> <model> [name] [--complex-model <model>] [--subscription-model <model>] [--subscription-reasoning <effort>]";
    public const string WorkspaceUsage = "Usage: workspace [create|merge|rebase|remove] [goal-id-prefix] [--force-terminal-cleanup]";
    public const string WorkspaceCreateUsage = "Usage: workspace create [goal-id-prefix]";
    public const string ReassignAgentUsage = "Usage: reassign-agent <task-number> <agent-id>|<goal-prefix> <task-number> <agent-id>|--goal <goal-prefix> <task-number> <agent-id>";
    public const string BacklogListUsage = "Usage: backlog-list [--all] [--limit <n>] [--status <value>] [--text <pattern>|--text=<leading-dash-pattern>]";
    public const string BacklogTriageUsage = "Usage: backlog-triage [--limit <n>] [--stale-days <n>]";
    public const string BacklogAddUsage = "Usage: backlog-add <title> [body] [--depends-on <id-prefix>] [--no-similar] | backlog-add --title <title> [--text-file <path>|--body-file <path>] [--depends-on <id-prefix>] [--no-similar] | backlog-add <title> --text-file <path> [--depends-on <id-prefix>] [--no-similar] | backlog-add <title> --body-file <path> [--depends-on <id-prefix>] [--no-similar]";
    public const string BacklogUpdateUsage = "Usage: backlog-update <id-prefix> [--title <text>] [--description <text>] [--priority <value>] [--tags <csv>] [--status <open|done|superseded>]";
    public const string BacklogShowUsage = "Usage: backlog-show <id-prefix>";
    public const string BacklogAnnotateUsage = "Usage: backlog-annotate <id-prefix> <note> | backlog-annotate <id-prefix> --text-file <path>";
    public const string BacklogCloseUsage = "Usage: backlog-close <id-prefix> [reason] | backlog-close <id-prefix> --reason-file <path> | backlog-close <id-prefix> --text-file <path>";
    public const string BacklogSupersedeUsage = "Usage: backlog-supersede <old-id-prefix> <new-id-prefix>";
    public const string BacklogUnsupersedeUsage = "Usage: backlog-unsupersede <id-prefix>";
    public const string BacklogLinkUsage = "Usage: backlog-link <canonical-id-prefix> <duplicate-id-prefix> [--related]";
    public const string BacklogDependsUsage = "Usage: backlog-depends <item-prefix> --on <prerequisite-prefix> | backlog-depends <item-prefix> --remove <prerequisite-prefix> | backlog-depends <item-prefix> --clear";
    public const string BacklogReopenUsage = "Usage: backlog-reopen <id-prefix> [reason]";
    public const string BacklogViewUsage = "Usage: backlog-view";
    public const string BacklogSimilarUsage = "Usage: backlog-similar <query text> [--limit <n>] [--status <value>] [--excerpt] | backlog-similar --id <backlog-id> [--limit <n>] [--status <value>] [--excerpt]";
    public const string EpicAddUsage = "Usage: epic-add <title> | epic-add --text-file <path>";
    public const string EpicAssignUsage = "Usage: epic-assign <goal-or-backlog-id> <epic>";
    public const string EpicListUsage = "Usage: epic-list";
    public const string EpicMembersUsage = "Usage: epic-members <epic>";
    public const string EpicSuggestUsage = "Usage: epic-suggest | epic-suggestions";
    public const string ProjectAddUsage = "Usage: project-add <title> | project-add --text-file <path>";
    public const string ProjectAssignUsage = "Usage: project-assign <epic> <project>";
    public const string PortfolioUsage = "Usage: portfolio | portfolio-view";
    public const string DogfoodLogUsage = "Usage: dogfood-log list [--limit <n>] | dogfood-log add [goal-prefix]";
    public const string OperatorCommandsUsage = "Usage: operator-commands [--help]";
    public const string GateStatusUsage = "Usage: gate-status";
    public const string TrialCompareUsage = "Usage: trial-compare --spec <path> [--receipts <directory>] [--timeout-seconds <seconds>] [--format text|json]. Spec source: explicit baseCommit + canonical workload, or historical goal/task/dispatch selector (mutually exclusive).";
    public const string AcceptanceEngineUsage = "Usage: acceptance-engine status | acceptance-engine clear <repair-or-operator-note>";
    public const string RunEventsMaintenanceUsage = "Usage: run-events-maintenance [--tick-max-age-days <days>] [--keep-tick-rows <count>] [--payload-max-bytes <bytes>] [--batch-size <rows>] [--legacy-purge-oversized-ticks] [--vacuum]";
    public const string StateDatabaseMaintenanceUsage = "Usage: state-db-maintenance plan | state-db-maintenance execute --confirm-offline | state-db-maintenance convert-copy --output <path> --confirm-offline | state-db-maintenance convert-live --confirm-offline --confirm-live-replacement";
    public const string RunEventUsage = "Usage: run-event show <sequence> [--format text|json]";
    public const string FlakeCensusUsage = "Usage: flake-census [--min-goals <n>] [--since <yyyy-MM-dd|ISO-8601-with-offset>]";

    private static readonly CommandHelpEntry Conduct = new(
        ConductUsage,
        "Drive one goal or run the autonomous conductor loop.",
        [
            "--policy",
            "--loop",
            "--max-iterations",
            "--max-duration",
            "--watch",
            "--daemon",
            "--poll-seconds",
            "--watch-interval",
            "--quiet",
            "--stall-warning-seconds",
            "--stall-warning-minutes",
            "--unscoped-stall-ticks",
            ConductorContinuitySupervisor.ChildFlag,
            ConductorContinuitySupervisor.ExitArtifactFlag,
            "--help",
            "-h"
        ]);

    private static readonly string[] HelpFlags = ["--help", "-h"];

    private static readonly string[] GoalRoleOverrideFlags =
        ["--ideation", "--researcher", "--planner", "--developer", "--tester", "--reviewer"];

    private static readonly string[] GoalOwnFlags =
    [
        "--pipeline", "--simple", "--from-backlog", "--run", "--confirm-batch-start",
        "--backlog-item", "--backlog-coverage", "--request-key", "--text-file", "--brief-file",
        ..GoalRoleOverrideFlags,
        ..HelpFlags
    ];

    private static readonly string[] SimpleGoalOwnFlags =
    [
        "--pipeline", "--brief-file", "--text-file", "--backlog-item", "--backlog-coverage",
        "--request-key", "--dispatch", "--confirm-dispatch-start",
        ..GoalRoleOverrideFlags,
        ..HelpFlags
    ];

    private static readonly string[] BacklogIntakeOwnFlags =
    [
        "--create-goal", "--create-simple-goal", "--force-reclaim", "--pipeline",
        "--backlog-item", "--backlog-coverage", "--request-key",
        ..HelpFlags
    ];

    private static readonly string[] AcceptanceOwnFlags =
    [
        "--skip-verify", "--keep-workspace", "--no-record", "--autonomy", "--autonomy-policy",
        ..HelpFlags
    ];

    private static readonly string[] RunGoalOwnFlags =
    [
        "--confirm-batch-start", "--confirm-large-paid-subscription-start", "--confirm-readiness-risk",
        "--autonomy", "--autonomy-policy",
        ..HelpFlags
    ];

    private static readonly CommandHelpEntry SimpleGoal = new(
        SimpleGoalUsage,
        "Create a developer-only goal.",
        SimpleGoalOwnFlags);

    private static readonly CommandHelpEntry BacklogIntake = new(
        BacklogIntakeUsage,
        "Plan or create goals from matching backlog items.",
        BacklogIntakeOwnFlags);

    private static readonly CommandHelpEntry Acceptance = new(
        AcceptanceUsage,
        "Run acceptance evidence and merge for a verified goal.",
        AcceptanceOwnFlags);

    private static readonly CommandHelpEntry RunGoal = new(
        RunGoalUsage,
        "Drive an existing goal through subscription dispatch until it is verified or blocked.",
        RunGoalOwnFlags);

    private static readonly CommandHelpEntry Goal = new(
        GoalUsage,
        "Create a goal. Before authoring criteria, assign evidence owners using docs/role-capability-matrix.md.",
        UnionFlags(GoalOwnFlags, SimpleGoal.Flags, BacklogIntake.Flags, RunGoal.Flags));

    private static readonly CommandHelpEntry GoalIntakeStatus = new(
        GoalIntakeStatusUsage,
        "Poll a keyed goal-intake request without starting or retrying work.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry GoalReplace = new(
        GoalReplaceUsage,
        "Atomically replace an eligible terminal goal while preserving source history.",
        [
            "--brief-file", "--reason-file", "--request-id", "--disposition", "--confirm-goal-replace", "--pipeline",
            "--ideation", "--researcher", "--planner", "--developer", "--tester", "--reviewer", "--help", "-h"
        ]);

    private static readonly CommandHelpEntry GoalsSubscribe = new(
        GoalMonitoringSubscriptionCommand.GoalsSubscribeUsage,
        "Monitor goal lifecycle events. Example: goals subscribe --goal-prefix abc123 --event-kind conductor:dispatch --wait-terminal --once --timeout 5m",
        ["--goal-prefix", "--from-cursor", "--since", "--task", "--event-kind", "--once", "--wait-terminal", "--format", "--timeout", "--help", "-h"]);

    private static readonly CommandHelpEntry GoalsBoard = new(
        GoalBoardUsage,
        "Show the read-only operational goal board for takeover and loop supervision.",
        ["--board", "--limit", "--all", "--help", "-h"]);

    private static readonly CommandHelpEntry AddTask = new(
        AddTaskUsage,
        "Add a task to the current or explicitly targeted goal.",
        ["--goal", "--before-role", "--text-file", "--help", "-h"]);

    private static readonly CommandHelpEntry Retry = new(
        RetryUsage,
        "Retry a task with operator feedback.",
        ["--goal", "--text-file", "--cause", "--mechanical", "--autonomy", "--autonomy-policy", "--idempotency-key", "--operator-actor", "--actor-kind", "--help", "-h"]);

    private static readonly CommandHelpEntry Note = new(
        NoteUsage,
        "Record a status-neutral task note.",
        ["--goal", "--text-file", "--gate-deliverable", "--help", "-h"]);

    private static readonly CommandHelpEntry Progress = new(
        ProgressUsage,
        "Record task progress.",
        ["--goal", "--text-file", "--idempotency-key", "--operator-actor", "--actor-kind", "--help", "-h"]);

    private static readonly CommandHelpEntry VerifyManual = new(
        VerifyManualUsage,
        "Record manual verification evidence for a task.",
        ["--goal", "--text-file", "--idempotency-key", "--operator-actor", "--actor-kind", "--help", "-h"]);

    private static readonly CommandHelpEntry Adjudicate = new(
        AdjudicateUsage,
        "Atomically close, reopen for a gate, or route a task with decision evidence. Evidence may be ref or id=hash.",
        ["--goal", "--text-file", "--evidence", "--cause", "--idempotency-key", "--operator-actor", "--actor-kind", "--help", "-h"]);

    public const string CriterionEvidenceMapUsage = "Usage: criterion-evidence-map --goal <goal-prefix> <criterion-index> <criterion-version> <acceptance|operator> <required-scope> <finding-stable-id> <candidate-sha> [--idempotency-key <key>] [--operator-actor <actor>]";
    public const string CriterionEvidenceRecordUsage = "Usage: criterion-evidence-record --goal <goal-prefix> <obligation-id> operator <candidate-sha> <receipt-id> <scope> <passed|failed> <detail> [--idempotency-key <key>] [--operator-actor <actor>]";
    public const string CriterionEvidenceRepairUsage = "Usage: criterion-evidence-repair --goal <goal-prefix> <malformed-obligation-id> <criterion-index> <criterion-version> <acceptance|operator> <required-scope> <finding-stable-id> <candidate-sha> <reason> [--idempotency-key <key>] [--operator-actor <actor>]";

    private static readonly CommandHelpEntry CriterionEvidenceMap = new(
        CriterionEvidenceMapUsage,
        "Queue a candidate-bound criterion ownership mapping for the conductor.",
        ["--goal", "--idempotency-key", "--operator-actor", "--help", "-h"]);

    private static readonly CommandHelpEntry CriterionEvidenceRecord = new(
        CriterionEvidenceRecordUsage,
        "Queue operator evidence for a mapped criterion. Acceptance evidence comes from the acceptance executor.",
        ["--goal", "--idempotency-key", "--operator-actor", "--help", "-h"]);

    private static readonly CommandHelpEntry CriterionEvidenceRepair = new(
        CriterionEvidenceRepairUsage,
        "Queue an attributed repair that rebinds one malformed Unknown claim; the replacement remains pending until valid evidence arrives.",
        ["--goal", "--idempotency-key", "--operator-actor", "--help", "-h"]);

    private static readonly CommandHelpEntry Recover = new(
        RecoverUsage,
        "Recover stuck goal/task state with an operator note.",
        ["--text-file", "--autonomy", "--autonomy-policy", "--help", "-h"]);

    private static readonly CommandHelpEntry AcceptanceRetry = new(
        AcceptanceRetryUsage,
        "Re-run an environmentally failed acceptance gate without retrying worker tasks.",
        ["--confirm-acceptance-retry", "--help", "-h"]);

    private static readonly CommandHelpEntry GoalAmend = new(
        GoalAmendUsage,
        "Waive one acceptance criterion on an in-flight goal with durable audit provenance.",
        ["--waive", "--reason", "--reason-file", "--text-file", "--disposition", "--disposition-file", "--actor", "--help", "-h"]);

    private static readonly CommandHelpEntry Revise = new(
        ReviseUsage,
        "Replace an active goal's authoritative brief while retaining its version history.",
        ["--brief-file", "--text-file", "--reason", "--reason-file", "--supersede-answer", "--history", "--help", "-h"]);

    private static readonly CommandHelpEntry Answer = new(
        AnswerUsage,
        "Submit an answer to a human-input request.",
        ["--text-file", "--gate-deliverable", "--help", "-h"]);

    private static readonly CommandHelpEntry Supersede = new(
        SupersedeUsage,
        "Replace a closed clarification answer while retaining its retracted audit history.",
        ["--text-file", "--help", "-h"]);

    private static readonly CommandHelpEntry GateSatisfied = new(
        GateSatisfiedUsage,
        "Explicitly lift a structured operator gate with durable evidence.",
        ["--text-file", "--help", "-h"]);

    private static readonly CommandHelpEntry Attention = new(
        AttentionUsage,
        "Show, dismiss, or answer operator attention items. Use top-level `answer` when supplying --gate-deliverable evidence.",
        ["--all", "--include-parked", "--goal", "--item", "--text-file", "--help", "-h"]);

    private static readonly CommandHelpEntry AbandonGoal = new(
        AbandonGoalUsage,
        "Abandon a goal after preview or confirmation.",
        ["--text-file", "--confirm-goal-abandon", "--help", "-h"]);

    private static readonly CommandHelpEntry CancelGoal = new(
        CancelGoalUsage,
        "Cancel a goal.",
        ["--text-file", "--confirm-goal-stop", "--help", "-h"]);

    private static readonly CommandHelpEntry SupersedeGoal = new(
        SupersedeGoalUsage,
        "Supersede a goal.",
        ["--text-file", "--confirm-goal-stop", "--help", "-h"]);

    private static readonly CommandHelpEntry ParkGoal = new(
        ParkGoalUsage,
        "Park a goal. Use unpark-goal to resume it later.",
        ["--text-file", "--confirm-goal-park", "--help", "-h"]);

    private static readonly CommandHelpEntry UnparkGoal = new(
        UnparkGoalUsage,
        "Resume a Parked goal.",
        ["--text-file", "--confirm-goal-unpark", "--help", "-h"]);

    private static readonly CommandHelpEntry Stop = new(
        StopUsage,
        "Route a goal stop disposition to cancel, park, abandon, or supersede.",
        ["--text-file", "--as", "--confirm-goal-stop", "--confirm-goal-park", "--confirm-goal-abandon", "--help", "-h"]);

    private static readonly CommandHelpEntry SubscriptionDispatch = new(
        SubscriptionDispatchUsage,
        "Prepare a subscription-backed task dispatch.",
        ["--goal", "--confirm-limit-review", "--text-file", "--subscription-model", "--subscription", "--subscription-reasoning", "--allow-git-reference", "--confirm-dispatch-start", "--confirm-large-paid-subscription-start", "--autonomy", "--autonomy-policy", "--confirm-readiness-risk", "--help", "-h"]);

    private static readonly CommandHelpEntry RefreshDispatch = new(
        RefreshDispatchUsage,
        "Reconcile one dispatch and print a compact decision surface; request durable task history explicitly.",
        ["--goal", "--history", "--history-limit", "--autonomy", "--autonomy-policy", "--help", "-h"]);

    private static readonly CommandHelpEntry Inquiry = new(
        InquiryUsage,
        "Ask a post-hoc question of a completed worker dispatch and write an advisory inquiry receipt.",
        ["--text-file", "--help", "-h"]);

    private static readonly CommandHelpEntry Agent = new(
        AgentUsage,
        "Replace the primary worker agent for a role.",
        ["--complex-model", "--subscription-model", "--subscription-reasoning", "--help", "-h"]);

    private static readonly CommandHelpEntry AgentAdd = new(
        AgentAddUsage,
        "Add or replace a same-role alternate worker agent by id.",
        ["--complex-model", "--subscription-model", "--subscription-reasoning", "--help", "-h"]);

    private static readonly CommandHelpEntry Workspace = new(
        WorkspaceUsage,
        "Manage goal worktrees for create, merge, rebase, and remove operations.",
        ["--autonomy", "--force-terminal-cleanup", "--help", "-h"]);

    private static readonly CommandHelpEntry WorkspaceCreate = new(
        WorkspaceCreateUsage,
        "Create or reuse the worktree for a goal.",
        ["--autonomy", "--help", "-h"]);

    private static readonly CommandHelpEntry ReassignAgent = new(
        ReassignAgentUsage,
        "Persist an exact agent assignment for a task.",
        ["--goal", "--help", "-h"]);

    private static readonly CommandHelpEntry BacklogList = new(
        BacklogListUsage,
        "List every backlog item with its status and linked goal id.",
        ["--all", "--limit", "--status", "--text", "--help", "-h"]);

    private static readonly CommandHelpEntry BacklogTriage = new(
        BacklogTriageUsage,
        "Print compact backlog triage buckets for daemon curation.",
        ["--limit", "--stale-days", "--help", "-h"]);

    private static readonly CommandHelpEntry BacklogAdd = new(
        BacklogAddUsage,
        "Add a backlog item. Prints advisory similarity pointers unless suppressed.",
        ["--title", "--text-file", "--body-file", "--depends-on", "--no-similar", "--help", "-h"]);

    private static readonly CommandHelpEntry BacklogUpdate = new(
        BacklogUpdateUsage,
        "Update user-settable backlog item fields by id prefix.",
        ["--title", "--description", "--priority", "--tags", "--status", "--help", "-h"]);

    private static readonly CommandHelpEntry BacklogShow = new(
        BacklogShowUsage,
        "Show a backlog item by id prefix.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry BacklogSimilar = new(
        BacklogSimilarUsage,
        "Rank related backlog items and completed goals using an invocation-local FTS5 index.",
        ["--id", "--limit", "--status", "--excerpt", "--help", "-h"]);

    private static readonly CommandHelpEntry BacklogAnnotate = new(
        BacklogAnnotateUsage,
        "Append a timestamped note to a backlog item by id prefix.",
        ["--text-file", "--help", "-h"]);

    private static readonly CommandHelpEntry BacklogClose = new(
        BacklogCloseUsage,
        "Close a backlog item by id prefix.",
        ["--reason-file", "--text-file", "--help", "-h"]);

    private static readonly CommandHelpEntry BacklogSupersede = new(
        BacklogSupersedeUsage,
        "Mark one backlog item as superseded by another existing item.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry BacklogUnsupersede = new(
        BacklogUnsupersedeUsage,
        "Clear a backlog item's supersede metadata.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry BacklogLink = new(
        BacklogLinkUsage,
        "Link duplicate or related backlog items.",
        ["--related", "--help", "-h"]);

    private static readonly CommandHelpEntry BacklogDepends = new(
        BacklogDependsUsage,
        "Add, remove, or clear directional backlog prerequisites.",
        ["--on", "--remove", "--clear", "--help", "-h"]);

    private static readonly CommandHelpEntry BacklogReopen = new(
        BacklogReopenUsage,
        "Reopen a backlog item by id prefix.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry BacklogView = new(
        BacklogViewUsage,
        "Render all backlog items as markdown.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry EpicAdd = new(
        EpicAddUsage,
        "Add a portfolio epic.",
        ["--text-file", "--help", "-h"]);

    private static readonly CommandHelpEntry EpicAssign = new(
        EpicAssignUsage,
        "Assign a goal or backlog item to one epic.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry EpicList = new(
        EpicListUsage,
        "List epics with membership and state rollups.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry EpicMembers = new(
        EpicMembersUsage,
        "List an epic's goal and backlog item members.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry EpicSuggest = new(
        EpicSuggestUsage,
        "Refresh or list advisory relatedness cluster suggestions.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry ProjectAdd = new(
        ProjectAddUsage,
        "Add a portfolio project.",
        ["--text-file", "--help", "-h"]);

    private static readonly CommandHelpEntry ProjectAssign = new(
        ProjectAssignUsage,
        "Assign an epic to one project.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry Portfolio = new(
        PortfolioUsage,
        "Render the project to epic to goal portfolio hierarchy.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry DogfoodLog = new(
        DogfoodLogUsage,
        "Read or add dogfood goal-boundary entries in the SQLite dogfood log store.",
        ["--limit", "--help", "-h"]);

    private static readonly CommandHelpEntry OperatorCommands = new(
        OperatorCommandsUsage,
        "List approved repo-bounded command prefixes for the normal operator loop.",
        ["--help", "-h"],
        [
            "Observe: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Get-OrchestratorSnapshot.ps1 -GoalPrefix <goal>",
            "Wait: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Wait-RepoInterval.ps1 -Seconds <n>",
            "Wait dispatch: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Wait-ForDispatch.ps1 -ExitFile <path>",
            "Git/diff: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Invoke-Git.ps1 status --short",
            "Backlog/orchestrator: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Invoke-OrchestratorCommand.ps1 backlog-list --limit <n>",
            "Process inspect: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Get-RepoProcessInfo.ps1 -Id <pid>",
            "Exact stop: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Stop-RepoProcess.ps1 -Id <pid>",
            "SQLite: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Invoke-OrchestratorSqliteTool.ps1 <args>",
            "Logs: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Show-OrchestratorLogArtifacts.ps1 -GoalPrefix <goal> [-TaskPrefix <task>] [-TailLines <n>]",
            "Acceptance: .\\scripts\\Invoke-RepoScript.ps1 scripts\\Invoke-OrchestratorCommand.ps1 acceptance <goal>"
        ]);

    private static readonly CommandHelpEntry GateStatus = new(
        GateStatusUsage,
        "List acceptance gate heartbeat status for stable build slots.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry AcceptanceEngine = new(
        AcceptanceEngineUsage,
        "Show or explicitly clear the post-landing canary circuit breaker.",
        ["--help", "-h"]);

    private static readonly CommandHelpEntry RunEventsMaintenance = new(
        RunEventsMaintenanceUsage,
        "Prune high-churn run-events.db conductor tick rows and optionally reclaim free pages when idle.",
        ["--tick-max-age-days", "--keep-tick-rows", "--payload-max-bytes", "--batch-size", "--legacy-purge-oversized-ticks", "--vacuum", "--help", "-h"]);

    private static readonly CommandHelpEntry StateDatabaseMaintenance = new(
        StateDatabaseMaintenanceUsage,
        "Plan/execute bounded reclamation, create a validated conversion copy, or explicitly replace state.db during an off-peak exclusive-maintenance window.",
        ["--confirm-offline", "--confirm-live-replacement", "--output", "--help", "-h"]);

    private static readonly CommandHelpEntry RunEvent = new(
        RunEventUsage,
        "Show one stored run event by sequence, including goal-less event receipt text.",
        ["--format", "--help", "-h"]);

    private static readonly CommandHelpEntry FlakeCensus = new(
        FlakeCensusUsage,
        "Summarize recurring test failures from the retained acceptance failing-test index.",
        ["--min-goals", "--since", "--help", "-h"]);

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> GenericCommandFlags =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["project"] = Flags("--root"),
            ["status"] = Flags("--tasks-only"),
            ["trial-compare"] = Flags("--spec", "--receipts", "--timeout-seconds", "--format"),
            ["hermes-acp-trial"] = Flags(
                "--confirm-live-hermes-start", "--prompt", "--prompt-sha256", "--workspace",
                "--sandbox", "--provider", "--model", "--role", "--receipt"),
            ["hermes-acp-verify-identity"] = Flags(
                "--executable", "--working-directory", "--hermes-home", "--receipt"),
            ["provider-smoke"] = Flags("--confirm-all", "--confirm-paid-smoke"),
            ["dashboard"] = DashboardFlags(),
            ["prototype-ui"] = DashboardFlags(),
            ["serve-dashboard"] = DashboardFlags(),
            ["hosted-dashboard"] = DashboardFlags(),
            ["simple-hosted-dashboard"] = DashboardFlags(),
            ["open-dashboard"] = DashboardFlags(),
            ["transcript"] = Flags(),
            ["monitor-goal"] = Flags(
                "--event-kind", "--format", "--from-cursor", "--goal-prefix", "--once", "--since",
                "--task", "--timeout", "--wait-terminal"),
            ["goal-events"] = Flags("--follow"),
            ["goal-timing"] = Flags("--all"),
            ["dispatch-value"] = Flags("--since"),
            ["durations"] = Flags("--by-model", "--since"),
            ["loop-health"] = Flags("--last"),
            ["repo-process-info"] = Flags(
                "--command-contains", "--conduct-loop", "--dispatch-host", "--id", "--include-children",
                "--locks", "--name", "--newest", "--parent-id"),
            ["repo-process-stop"] = Flags("--command-contains", "--force", "--id"),
            ["failure-triage"] = Flags("--autonomy", "--autonomy-policy", "--policy"),
            ["build-lease-cleanup"] = Flags("--confirm-build-lease-cleanup"),
            ["acceptance-queue"] = Flags(
                "--apply", "--autonomy", "--autonomy-policy", "--confirm-acceptance-queue"),
            ["drain-goals"] = Flags(
                "--apply", "--autonomy", "--autonomy-policy", "--confirm-batch-start", "--confirm-goal-drain",
                "--confirm-large-paid-subscription-start", "--confirm-readiness-risk"),
            ["operator-inbox"] = Flags("--show-acknowledged"),
            ["operator-inbox-ack"] = Flags("--goal"),
            ["operator-channel"] = Flags(
                "--dashboard-url", "--forum-channel-id", "--operator-user-id", "--operator-user-ids", "--spine"),
            ["operator-control-plane"] = Flags("--hours"),
            ["next"] = Flags("--autonomy", "--autonomy-policy", "--full"),
            ["advance-subscription"] = Flags(
                "--autonomy", "--autonomy-policy", "--confirm-large-paid-subscription-start",
                "--confirm-subscription-advance"),
            ["lifecycle-simple-goal"] = LifecycleGoalFlags(),
            ["lifecycle-goal"] = LifecycleGoalFlags(),
            ["goal-depends"] = Flags("--clear", "--on", "--remove"),
            ["goal-plan"] = Flags("--backlog-coverage", "--create-goals", "--create-simple-goals"),
            ["plan"] = Flags("--confirm-plan", "--slice-batch"),
            ["ideate"] = Flags("--append-backlog"),
            ["intent-template"] = Flags("--create-goal", "--create-simple-goal"),
            ["goal-mark-landed"] = Flags("--confirm-goal-mark-landed", "--force"),
            ["acceptance-repair"] = Flags("--confirm-acceptance-repair"),
            ["rollback-goal"] = Flags("--confirm-goal-rollback", "--text-file"),
            ["goal-changes"] = Flags("--all", "--committed", "--flat", "--json", "--role", "--task", "--working"),
            ["goals-prune"] = Flags("--confirm-prune"),
            ["model-function-add"] = Flags("--subscription", "--subscription-model", "--subscription-reasoning"),
            ["task"] = GoalScopedTaskFlags(),
            ["verification-plan"] = GoalScopedTaskFlags(),
            ["brief"] = GoalScopedTaskFlags(),
            ["task-timeline"] = GoalScopedTaskFlags(),
            ["verifications"] = GoalScopedTaskFlags(),
            ["run"] = ApiRunFlags(),
            ["api-run"] = ApiRunFlags(),
            ["re-delegate"] = Flags("--autonomy", "--autonomy-policy", "--goal"),
            ["redelegate"] = Flags("--autonomy", "--autonomy-policy", "--goal"),
            ["profile-dispatch"] = DispatchStartFlags("--confirm-dispatch-start"),
            ["profile-dispatch-ready"] = Flags("--goal"),
            ["subscription-dispatch-ready"] = Flags("--goal"),
            ["cross-goal-start-plan"] = Flags("--confirm-large-paid-subscription-start"),
            ["start-subscription-ready-goals"] = BatchStartFlags(includeGoal: false),
            ["start-subscription-ready"] = BatchStartFlags(includeGoal: true),
            ["execute-dispatch"] = DispatchStartFlags("--confirm-dispatch-start"),
            ["start-dispatch"] = DispatchStartFlags("--confirm-dispatch-start"),
            ["start-dispatches"] = BatchStartFlags(includeGoal: true),
            ["refresh-dispatches"] = Flags("--autonomy", "--autonomy-policy", "--goal"),
            ["logs"] = GoalScopedTaskFlags(),
            ["cancel-dispatch"] = GoalScopedTaskFlags(),
            ["accept"] = Flags(
                "--autonomy", "--autonomy-policy", "--keep-workspace", "--no-record", "--skip-verify")
        };

    private static IReadOnlySet<string> Flags(params string[] flags) =>
        flags.Concat(["--help", "-h"]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> DashboardFlags() =>
        Flags("--lan", "--mode", "--no-open", "--open", "--refresh");

    private static IReadOnlySet<string> LifecycleGoalFlags() =>
        Flags(
            "--autonomy", "--autonomy-policy", "--backlog-coverage", "--backlog-item", "--confirm-batch-start",
            "--confirm-large-paid-subscription-start", "--confirm-readiness-risk");

    private static IReadOnlySet<string> GoalScopedTaskFlags() => Flags("--goal");

    private static IReadOnlySet<string> ApiRunFlags() =>
        Flags(
            "--autonomy", "--autonomy-policy", "--confirm-large-paid-api-prompt", "--confirm-paid-api-run", "--goal");

    private static IReadOnlySet<string> DispatchStartFlags(string confirmationFlag) =>
        Flags(
            "--autonomy", "--autonomy-policy", "--confirm-large-paid-subscription-start", confirmationFlag, "--goal");

    private static IReadOnlySet<string> BatchStartFlags(bool includeGoal)
    {
        var flags = new List<string>
        {
            "--autonomy", "--autonomy-policy", "--confirm-batch-start",
            "--confirm-large-paid-subscription-start", "--confirm-readiness-risk"
        };
        if (includeGoal)
        {
            flags.Add("--goal");
        }

        return Flags([.. flags]);
    }

    private static readonly IReadOnlySet<string> InlineValueFlags =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--text", "--request-key", "--brief-file", "--text-file", "--pipeline",
            "--backlog-item", "--backlog-coverage", "--ideation", "--researcher", "--planner",
            "--developer", "--tester", "--reviewer"
        };

    public static bool TryPrintStartupHelp(IReadOnlyList<string> args)
    {
        if (args.Count == 1 && IsHelpFlag(args[0]))
        {
            PrintRootHelp();
            return true;
        }

        if (TryPrintHelpCommand(args))
        {
            return true;
        }

        if (!TryResolveEntry(args, out var entry) || !HasHelpFlag(args))
        {
            return false;
        }

        Print(entry);
        return true;
    }

    private static void PrintRootHelp()
    {
        Console.WriteLine("Usage: mcg-orchestrator <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  goal              Create a goal.");
        Console.WriteLine("  goal-intake-status Poll keyed goal creation status.");
        Console.WriteLine("  goal-replace      Replace an eligible terminal source-linked goal.");
        Console.WriteLine("  goals             List goals.");
        Console.WriteLine("  status            Show goal or orchestrator status.");
        Console.WriteLine("  attention         List or answer operator attention items.");
        Console.WriteLine("  backlog-list      List backlog items.");
        Console.WriteLine("  backlog-add       Add a backlog item.");
        Console.WriteLine("  backlog-similar   Rank related backlog items and completed goals.");
        Console.WriteLine("  retry             Retry a task with operator feedback.");
        Console.WriteLine("  recover           Recover a goal with an operator note.");
        Console.WriteLine("  conduct           Drive one goal or the autonomous loop.");
        Console.WriteLine("  help <command>    Show command-specific help.");
    }

    internal static bool IsCommandSpecificHelp(IReadOnlyList<string> args)
    {
        return TryResolveHelpCommand(args, out _, out _) || (HasHelpFlag(args) && TryResolveEntry(args, out _));
    }

    internal static void ThrowIfInvalidFlags(IReadOnlyList<string> args)
    {
        if (!TryResolveEntry(args, out var entry))
        {
            return;
        }

        if (!entry.ValidateFlags)
        {
            return;
        }

        for (var index = 1; index < args.Count; index++)
        {
            var arg = args[index];
            if (!IsFlag(arg))
            {
                continue;
            }

            var option = GetOptionName(arg);
            if (!entry.Flags.Contains(option))
            {
                var previousOption = index > 1 ? GetOptionName(args[index - 1]) : null;
                var escapeHint = previousOption is not null &&
                    InlineValueFlags.Contains(previousOption) &&
                    entry.Flags.Contains(previousOption)
                    ? $" To pass a leading-dash value, use {previousOption}={arg}."
                    : string.Empty;
                var suggestions = CliArgumentParser.FindNearestFlags(option, entry.Flags);
                var suggestionText = suggestions.Count > 0
                    ? $"{Environment.NewLine}Did you mean: {string.Join(", ", suggestions)}?"
                    : string.Empty;
                throw new ArgumentException($"Unknown option '{arg}'.{escapeHint}{suggestionText}{Environment.NewLine}{entry.Usage}");
            }
        }
    }

    private static string GetOptionName(string arg)
    {
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            return arg;
        }

        var separatorIndex = arg.IndexOf('=');
        if (separatorIndex <= 2)
        {
            return arg;
        }

        var option = arg[..separatorIndex];
        return InlineValueFlags.Contains(option) ? option : arg;
    }

    private static bool TryResolveEntry(IReadOnlyList<string> args, out CommandHelpEntry entry)
    {
        entry = default;
        if (args.Count == 0)
        {
            return false;
        }

        if (args[0].Equals("conduct", StringComparison.OrdinalIgnoreCase))
        {
            entry = Conduct;
            return true;
        }

        if (args[0].Equals("goal", StringComparison.OrdinalIgnoreCase))
        {
            entry = Goal;
            return true;
        }

        if (args[0].Equals("simple-goal", StringComparison.OrdinalIgnoreCase))
        {
            entry = SimpleGoal;
            return true;
        }

        if (args[0].Equals("backlog-intake", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogIntake;
            return true;
        }

        if (args[0].Equals("acceptance", StringComparison.OrdinalIgnoreCase))
        {
            entry = Acceptance;
            return true;
        }

        if (args[0].Equals("run-goal", StringComparison.OrdinalIgnoreCase))
        {
            entry = RunGoal;
            return true;
        }

        if (args[0].Equals("goal-replace", StringComparison.OrdinalIgnoreCase))
        {
            entry = GoalReplace;
            return true;
        }

        if (args[0].Equals("goal-intake-status", StringComparison.OrdinalIgnoreCase))
        {
            entry = GoalIntakeStatus;
            return true;
        }

        if (IsGoalsBoardHelpTarget(args))
        {
            entry = GoalsBoard;
            return true;
        }

        if (IsGoalsSubscribeHelpTarget(args))
        {
            entry = GoalsSubscribe;
            return true;
        }

        if (args[0].Equals("add-task", StringComparison.OrdinalIgnoreCase))
        {
            entry = AddTask;
            return true;
        }

        if (args[0].Equals("retry", StringComparison.OrdinalIgnoreCase))
        {
            entry = Retry;
            return true;
        }

        if (args[0].Equals("note", StringComparison.OrdinalIgnoreCase))
        {
            entry = Note;
            return true;
        }

        if (args[0].Equals("progress", StringComparison.OrdinalIgnoreCase))
        {
            entry = Progress;
            return true;
        }

        if (args[0].Equals("verify-manual", StringComparison.OrdinalIgnoreCase))
        {
            entry = VerifyManual;
            return true;
        }

        if (args[0].Equals("adjudicate", StringComparison.OrdinalIgnoreCase))
        {
            entry = Adjudicate;
            return true;
        }

        if (args[0].Equals("criterion-evidence-map", StringComparison.OrdinalIgnoreCase))
        {
            entry = CriterionEvidenceMap;
            return true;
        }

        if (args[0].Equals("criterion-evidence-record", StringComparison.OrdinalIgnoreCase))
        {
            entry = CriterionEvidenceRecord;
            return true;
        }

        if (args[0].Equals("criterion-evidence-repair", StringComparison.OrdinalIgnoreCase))
        {
            entry = CriterionEvidenceRepair;
            return true;
        }

        if (args[0].Equals("recover", StringComparison.OrdinalIgnoreCase))
        {
            entry = Recover;
            return true;
        }

        if (args[0].Equals("acceptance-retry", StringComparison.OrdinalIgnoreCase))
        {
            entry = AcceptanceRetry;
            return true;
        }

        if (args[0].Equals("goal-amend", StringComparison.OrdinalIgnoreCase))
        {
            entry = GoalAmend;
            return true;
        }

        if (args[0].Equals("revise", StringComparison.OrdinalIgnoreCase))
        {
            entry = Revise;
            return true;
        }

        if (args[0].Equals("answer", StringComparison.OrdinalIgnoreCase))
        {
            entry = Answer;
            return true;
        }

        if (args[0].Equals("supersede", StringComparison.OrdinalIgnoreCase))
        {
            entry = Supersede;
            return true;
        }

        if (args[0].Equals("gate-satisfied", StringComparison.OrdinalIgnoreCase))
        {
            entry = GateSatisfied;
            return true;
        }

        if (args[0].Equals("attention", StringComparison.OrdinalIgnoreCase))
        {
            entry = Attention;
            return true;
        }

        if (args[0].Equals("run-events-maintenance", StringComparison.OrdinalIgnoreCase))
        {
            entry = RunEventsMaintenance;
            return true;
        }

        if (args[0].Equals("state-db-maintenance", StringComparison.OrdinalIgnoreCase))
        {
            entry = StateDatabaseMaintenance;
            return true;
        }

        if (args[0].Equals("run-event", StringComparison.OrdinalIgnoreCase))
        {
            entry = RunEvent;
            return true;
        }

        if (args[0].Equals("flake-census", StringComparison.OrdinalIgnoreCase))
        {
            entry = FlakeCensus;
            return true;
        }

        if (args[0].Equals("abandon-goal", StringComparison.OrdinalIgnoreCase))
        {
            entry = AbandonGoal;
            return true;
        }

        if (args[0].Equals("cancel-goal", StringComparison.OrdinalIgnoreCase))
        {
            entry = CancelGoal;
            return true;
        }

        if (args[0].Equals("supersede-goal", StringComparison.OrdinalIgnoreCase))
        {
            entry = SupersedeGoal;
            return true;
        }

        if (args[0].Equals("park-goal", StringComparison.OrdinalIgnoreCase))
        {
            entry = ParkGoal;
            return true;
        }

        if (args[0].Equals("unpark-goal", StringComparison.OrdinalIgnoreCase))
        {
            entry = UnparkGoal;
            return true;
        }

        if (args[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            entry = Stop;
            return true;
        }

        if (args[0].Equals("subscription-dispatch", StringComparison.OrdinalIgnoreCase))
        {
            entry = SubscriptionDispatch;
            return true;
        }

        if (args[0].Equals("refresh-dispatch", StringComparison.OrdinalIgnoreCase))
        {
            entry = RefreshDispatch;
            return true;
        }

        if (args[0].Equals("inquiry", StringComparison.OrdinalIgnoreCase))
        {
            entry = Inquiry;
            return true;
        }

        if (args[0].Equals("agent", StringComparison.OrdinalIgnoreCase))
        {
            entry = Agent;
            return true;
        }

        if (args[0].Equals("agent-add", StringComparison.OrdinalIgnoreCase))
        {
            entry = AgentAdd;
            return true;
        }

        if (args[0].Equals("reassign-agent", StringComparison.OrdinalIgnoreCase))
        {
            entry = ReassignAgent;
            return true;
        }

        if (args[0].Equals("backlog-list", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogList;
            return true;
        }

        if (args[0].Equals("backlog-triage", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogTriage;
            return true;
        }

        if (args[0].Equals("backlog-add", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogAdd;
            return true;
        }

        if (args[0].Equals("backlog-update", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogUpdate;
            return true;
        }

        if (args[0].Equals("backlog-show", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogShow;
            return true;
        }

        if (args[0].Equals("backlog-annotate", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogAnnotate;
            return true;
        }

        if (args[0].Equals("backlog-close", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogClose;
            return true;
        }

        if (args[0].Equals("backlog-supersede", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogSupersede;
            return true;
        }

        if (args[0].Equals("backlog-unsupersede", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogUnsupersede;
            return true;
        }

        if (args[0].Equals("backlog-link", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogLink;
            return true;
        }

        if (args[0].Equals("backlog-depends", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogDepends;
            return true;
        }

        if (args[0].Equals("backlog-reopen", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogReopen;
            return true;
        }

        if (args[0].Equals("backlog-view", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogView;
            return true;
        }

        if (args[0].Equals("backlog-similar", StringComparison.OrdinalIgnoreCase))
        {
            entry = BacklogSimilar;
            return true;
        }

        if (args[0].Equals("epic-add", StringComparison.OrdinalIgnoreCase))
        {
            entry = EpicAdd;
            return true;
        }

        if (args[0].Equals("epic-assign", StringComparison.OrdinalIgnoreCase))
        {
            entry = EpicAssign;
            return true;
        }

        if (args[0].Equals("epic-list", StringComparison.OrdinalIgnoreCase))
        {
            entry = EpicList;
            return true;
        }

        if (args[0].Equals("epic-members", StringComparison.OrdinalIgnoreCase))
        {
            entry = EpicMembers;
            return true;
        }

        if (args[0].Equals("epic-suggest", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("epic-suggestions", StringComparison.OrdinalIgnoreCase))
        {
            entry = EpicSuggest;
            return true;
        }

        if (args[0].Equals("project-add", StringComparison.OrdinalIgnoreCase))
        {
            entry = ProjectAdd;
            return true;
        }

        if (args[0].Equals("project-assign", StringComparison.OrdinalIgnoreCase))
        {
            entry = ProjectAssign;
            return true;
        }

        if (args[0].Equals("portfolio", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("portfolio-view", StringComparison.OrdinalIgnoreCase))
        {
            entry = Portfolio;
            return true;
        }

        if (args[0].Equals("dogfood-log", StringComparison.OrdinalIgnoreCase))
        {
            entry = DogfoodLog;
            return true;
        }

        if (args[0].Equals("operator-commands", StringComparison.OrdinalIgnoreCase))
        {
            entry = OperatorCommands;
            return true;
        }

        if (args[0].Equals("gate-status", StringComparison.OrdinalIgnoreCase))
        {
            entry = GateStatus;
            return true;
        }

        if (args[0].Equals("acceptance-engine", StringComparison.OrdinalIgnoreCase))
        {
            entry = AcceptanceEngine;
            return true;
        }

        if (!args[0].Equals("workspace", StringComparison.OrdinalIgnoreCase))
        {
            if (IsDashboardAdapterCommand(args[0]) || CliArgumentParser.IsRecognizedCommand(args[0]))
            {
                entry = CommandHelpEntry.Generic(args[0]);
                return true;
            }

            return false;
        }

        if (args.Count > 1 && args[1].Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            entry = WorkspaceCreate;
            return true;
        }

        entry = Workspace;
        return true;
    }

    private static bool IsDashboardAdapterCommand(string command) =>
        command.Equals("dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("serve-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("simple-hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("open-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("prototype-ui", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("transcript", StringComparison.OrdinalIgnoreCase);

    private static bool TryPrintHelpCommand(IReadOnlyList<string> args)
    {
        if (!TryResolveHelpCommand(args, out var target, out var entry))
        {
            return false;
        }

        if (entry is { } resolved)
        {
            Print(resolved);
            return true;
        }

        throw new ArgumentException(CliArgumentParser.FormatUnknownCommandMessage([target]));
    }

    private static bool TryResolveHelpCommand(
        IReadOnlyList<string> args,
        out string target,
        out CommandHelpEntry? entry)
    {
        target = "";
        entry = null;
        if (args.Count < 2 || !args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var targetParts = ExpandHelpTarget(args.Skip(1)).ToArray();
        target = string.Join(' ', targetParts);
        if (TryResolveEntry(targetParts, out var resolved))
        {
            entry = resolved;
        }

        return true;
    }

    private static void Print(CommandHelpEntry entry)
    {
        Console.WriteLine(entry.Usage);
        Console.WriteLine();
        Console.WriteLine(entry.Description);
        Console.WriteLine();
        Console.WriteLine("Options:");
        var flags = entry.Flags
            .Where(flag => flag.StartsWith("-", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (flags.Contains("-h", StringComparer.OrdinalIgnoreCase) &&
            flags.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("  -h, --help");
            flags = flags
                .Where(flag => !flag.Equals("-h", StringComparison.OrdinalIgnoreCase) &&
                               !flag.Equals("--help", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        foreach (var flag in flags)
        {
            if (flag.Equals("--poll-seconds", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {flag} <n>    Positive integer seconds between watch polls; default {ConductorBatchLoop.DefaultWatchIntervalSeconds}.");
            }
            else if (flag.Equals("--watch-interval", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {flag} <n>    Legacy alias for --poll-seconds.");
            }
            else if (flag.Equals("--unscoped-stall-ticks", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {flag} <n>    Consecutive unscoped dispatchable ticks before auto-rescope; default {ConductorBatchLoop.DefaultUnscopedStallTickThreshold}.");
            }
            else if (flag.Equals("--limit", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {flag} <n>");
            }
            else if (flag.Equals("--history-limit", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {flag} <n>    Include only the newest n task-history events, in chronological order.");
            }
            else if (flag.Equals("--history", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {flag}        Include the complete durable task history.");
            }
            else if (flag.Equals("--tasks-only", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {flag}        Show only the goal identifier, status, and task list.");
            }
            else if (flag.Equals("--status", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {flag} <value>");
            }
            else if (flag.Equals("--text", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {flag} <pattern>");
            }
            else
            {
                Console.WriteLine($"  {flag}");
            }
        }

        if (entry.ExtraLines.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Approved prefixes:");
            foreach (var line in entry.ExtraLines)
            {
                Console.WriteLine($"  {line}");
            }
        }
    }

    private static bool HasHelpFlag(IReadOnlyList<string> args)
    {
        return args.Any(IsHelpFlag) ||
            ((IsGoalsSubscribeHelpTarget(args) || IsGoalsBoardHelpTarget(args)) && SplitCompositeToken(args[1]).Any(IsHelpFlag));
    }

    private static bool IsGoalsSubscribeHelpTarget(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !args[0].Equals("goals", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return SplitCompositeToken(args[1]).FirstOrDefault()?.Equals("subscribe", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsGoalsBoardHelpTarget(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !args[0].Equals("goals", StringComparison.OrdinalIgnoreCase))
            return false;
        return SplitCompositeToken(args[1]).FirstOrDefault()?.Equals("--board", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static IEnumerable<string> ExpandHelpTarget(IEnumerable<string> targetParts)
    {
        var parts = targetParts.ToArray();
        return parts.Length == 1 ? SplitCompositeToken(parts[0]) : parts;
    }

    private static IReadOnlyList<string> SplitCompositeToken(string value) =>
        value.Contains(' ')
            ? CliArgumentParser.SplitCommand(value)
            : [value];

    private static bool IsHelpFlag(string arg) =>
        arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("-h", StringComparison.OrdinalIgnoreCase);

    private static bool IsFlag(string arg) =>
        !arg.Equals("-", StringComparison.Ordinal) &&
        arg.StartsWith("-", StringComparison.Ordinal) &&
        !int.TryParse(arg, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _);

    private readonly record struct CommandHelpEntry(
        string Usage,
        string Description,
        IReadOnlySet<string> Flags,
        IReadOnlyList<string> ExtraLines,
        bool ValidateFlags)
    {
        public CommandHelpEntry(string usage, string description, IEnumerable<string> flags)
            : this(usage, description, flags.ToHashSet(StringComparer.OrdinalIgnoreCase), [], ValidateFlags: true)
        {
        }

        public CommandHelpEntry(string usage, string description, IEnumerable<string> flags, IReadOnlyList<string> extraLines)
            : this(usage, description, flags.ToHashSet(StringComparer.OrdinalIgnoreCase), extraLines, ValidateFlags: true)
        {
        }

        public CommandHelpEntry(string usage, string description, IReadOnlySet<string> flags, bool ValidateFlags)
            : this(usage, description, flags, [], ValidateFlags)
        {
        }

        public static CommandHelpEntry Generic(string command)
        {
            // stable-slot-dotnet forwards every argument after the command to dotnet, whose option domain
            // is intentionally open. Those tokens are not orchestrator flags and cannot be enumerated here.
            var forwardsArbitraryDotnetOptions = command.Equals("stable-slot-dotnet", StringComparison.OrdinalIgnoreCase);
            return new CommandHelpEntry(
                $"Usage: {command} [options]",
                "Run this operator command.",
                GenericCommandFlags.TryGetValue(command, out var flags) ? flags : CliCommandHelp.Flags(),
                [],
                ValidateFlags: !forwardsArbitraryDotnetOptions);
        }
    }

    private static HashSet<string> UnionFlags(params IEnumerable<string>[] parts)
    {
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
        {
            flags.UnionWith(part);
        }

        return flags;
    }
}

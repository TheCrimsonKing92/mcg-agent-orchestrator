using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
private static void AppendGoalAliasFlags(
    IReadOnlyList<string> parts,
    List<string> target,
    bool includeRoleAgentFlags,
    params string[] excludedFlags)
{
    for (var i = 2; i < parts.Count; i++)
    {
        var part = parts[i];
        if (!part.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        if (excludedFlags.Any(flag =>
                part.Equals(flag, StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase)) ||
            (!includeRoleAgentFlags && GoalRoleAgentFlags.ContainsKey(part)))
        {
            if (IsCliValueFlag(part))
            {
                i++;
            }
            continue;
        }

        target.Add(part);
        if (IsCliValueFlag(part) && i + 1 < parts.Count)
        {
            target.Add(parts[++i]);
        }
    }
}

private static SourceBacklogItemLink? ResolveSourceBacklogItemLink(
    CliExecutionContext context,
    IReadOnlyList<string> parts,
    string objective)
{
    var explicitPrefix = GetFlagValue(parts, "--backlog-item");
    if (explicitPrefix is null && HasCliConfirmation(parts, "--backlog-item"))
    {
        throw new ArgumentException("--backlog-item requires an id prefix.");
    }

    if (!string.IsNullOrWhiteSpace(explicitPrefix))
    {
        var explicitItem = ResolveBacklogItemIdPrefix(context.Workspace.BacklogStorePath, explicitPrefix, explicitFlag: true)!;
        ValidateBacklogPromotionPrerequisites(context, explicitItem);
        ValidateSourceBacklogItemAvailable(context, explicitItem);
        return new SourceBacklogItemLink(
            explicitItem,
            FromExplicitFlag: true,
            ResolveSourceBacklogCoverage(parts, sourceLinkDeclared: true)!.Value);
    }

    var match = OpeningBacklogObjectiveReferenceRegex.Match(objective);
    if (!match.Success)
    {
        ResolveSourceBacklogCoverage(parts, sourceLinkDeclared: false);
        return null;
    }

    var item = ResolveBacklogItemIdPrefix(context.Workspace.BacklogStorePath, match.Groups[1].Value, explicitFlag: false);
    if (item is not null)
    {
        ValidateBacklogPromotionPrerequisites(context, item);
        ValidateSourceBacklogItemAvailable(context, item);
    }
    if (item is null)
    {
        ResolveSourceBacklogCoverage(parts, sourceLinkDeclared: false);
        return null;
    }

    return new SourceBacklogItemLink(
        item,
        FromExplicitFlag: false,
        ResolveSourceBacklogCoverage(parts, sourceLinkDeclared: true)!.Value);
}

private static void ValidateSourceBacklogItemAvailable(CliExecutionContext context, BacklogItem item)
{
    if (context.Kernel.FindGoalBySourceBacklogItemId(item.Id) is { } competingGoal)
    {
        throw new InvalidOperationException(
            $"GOAL_CREATE_PRECONDITION_CHANGED reason=source-backlog-consumed backlogItem={item.Id} competingGoal={competingGoal.Id.Value}");
    }
}

private static SourceBacklogCoverage? ResolveSourceBacklogCoverage(
    IReadOnlyList<string> parts,
    bool sourceLinkDeclared)
{
    const string flag = "--backlog-coverage";
    var flagPresent = HasCliConfirmation(parts, flag);
    var value = GetFlagValue(parts, flag);
    if (flagPresent && (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal)))
        throw new ArgumentException("--backlog-coverage requires full or slice.");

    if (!sourceLinkDeclared)
    {
        if (flagPresent)
            throw new ArgumentException("--backlog-coverage requires a source backlog item link.");
        return null;
    }

    if (!flagPresent)
        throw new ArgumentException("A source backlog item link requires --backlog-coverage full or slice.");

    return value!.Trim().ToLowerInvariant() switch
    {
        "full" => SourceBacklogCoverage.Full,
        "slice" => SourceBacklogCoverage.Slice,
        _ => throw new ArgumentException("--backlog-coverage must be full or slice.")
    };
}

private static BacklogItem? ResolveBacklogItemIdPrefix(string backlogStorePath, string idPrefix, bool explicitFlag)
{
    var trimmed = idPrefix.Trim();
    if (string.IsNullOrWhiteSpace(trimmed))
    {
        if (explicitFlag)
        {
            throw new ArgumentException("--backlog-item requires an id prefix.");
        }

        return null;
    }

    try
    {
        var item = new BacklogStore(backlogStorePath).GetByIdPrefixAsync(trimmed).GetAwaiter().GetResult();
        if (item is null && explicitFlag)
        {
            throw new InvalidOperationException($"No backlog item found with id prefix '{trimmed}'.");
        }

        return item;
    }
    catch (InvalidOperationException) when (!explicitFlag)
    {
        throw;
    }
}

private static void ApplySourceBacklogItemLink(CliExecutionContext context, Goal goal, SourceBacklogItemLink? link)
{
    if (link is null)
    {
        return;
    }

    if (context.Kernel.FindGoalBySourceBacklogItemId(link.Item.Id) is { } competingGoal &&
        competingGoal.Id != goal.Id)
    {
        throw new InvalidOperationException(
            $"GOAL_CREATE_PRECONDITION_CHANGED reason=source-backlog-consumed backlogItem={link.Item.Id} competingGoal={competingGoal.Id.Value}");
    }

    if (!string.IsNullOrWhiteSpace(goal.SourceBacklogItemId) &&
        !string.Equals(goal.SourceBacklogItemId, link.Item.Id, StringComparison.Ordinal))
    {
        Console.WriteLine($"Warning: goal {goal.Id.Value[..8]} is already linked to backlog item {goal.SourceBacklogItemId}; requested link {link.Item.Id} ignored.");
        return;
    }

    context.Kernel.SetGoalSourceBacklogItemLink(goal.Id, link.Item.Id, link.Coverage);
    ApplyBacklogPromotionDependencies(context, goal, link.Item);
}

private static void ValidateBacklogPromotionPrerequisites(CliExecutionContext context, BacklogItem item)
{
    foreach (var dependency in item.Dependencies)
        ResolvePromotedDependencyGoal(context, dependency);
}

private static void ApplyBacklogPromotionDependencies(
    CliExecutionContext context,
    Goal goal,
    BacklogItem item)
{
    foreach (var dependency in item.Dependencies)
        context.Kernel.SetGoalDependency(goal.Id, ResolvePromotedDependencyGoal(context, dependency).Id);
}

private static Goal ResolvePromotedDependencyGoal(
    CliExecutionContext context,
    BacklogDependency dependency)
{
    var goal = dependency.TargetKind == BacklogDependencyTargetKind.Goal
        ? context.Kernel.Goals.FirstOrDefault(candidate => candidate.Id.Value == dependency.PrerequisiteId)
        : context.Kernel.FindGoalBySourceBacklogItemId(dependency.PrerequisiteId);
    if (goal is not null)
        return goal;

    var reason = dependency.TargetKind == BacklogDependencyTargetKind.Backlog
        ? $"waiting on open prerequisite {dependency.PrerequisiteId}; promote that backlog item first"
        : $"dependency goal {dependency.PrerequisiteId} is unavailable";
    throw new InvalidOperationException($"Cannot promote dependent backlog item: {reason}.");
}

private static void PrintClosedSourceBacklogWarning(SourceBacklogItemLink? link)
{
    if (link?.Item.Status == BacklogItemStatus.Done)
    {
        Console.WriteLine($"Warning: linked backlog item {link.Item.Id} is already Done; goal creation will continue but may recreate landed work.");
    }
}

private static List<string> GetBacklogIntakeFilters(CliExecutionContext context, IReadOnlyList<string> parts)
{
    if (ResolveBacklogIntakeExplicitItemId(context, parts) is { } explicitItemId)
    {
        return [explicitItemId];
    }

    var filters = new List<string>();
    for (var i = 1; i < parts.Count; i++)
    {
        var part = parts[i];
        if (IsCliValueFlag(part))
        {
            i++;
            continue;
        }

        if (!part.StartsWith("--", StringComparison.Ordinal))
        {
            filters.Add(part);
        }
    }

    return filters;
}

private static string? ResolveBacklogIntakeFilter(CliExecutionContext context, IReadOnlyList<string> parts, params string[] ignoredFlags)
{
    return ResolveBacklogIntakeExplicitItemId(context, parts) ?? GetOptionalArgument(parts, ignoredFlags);
}

private static string? ResolveBacklogIntakeExplicitItemId(CliExecutionContext context, IReadOnlyList<string> parts)
{
    var explicitPrefix = GetFlagValue(parts, "--backlog-item");
    if (explicitPrefix is null)
    {
        if (HasCliConfirmation(parts, "--backlog-item"))
        {
            throw new ArgumentException("--backlog-item requires an id prefix.");
        }

        return null;
    }

    return ResolveBacklogItemIdPrefix(context.Workspace.BacklogStorePath, explicitPrefix, explicitFlag: true)!.Id;
}

private static bool HandleBacklogIntake(CliExecutionContext context, IReadOnlyList<string> parts)
{
    var createGoal = HasCliConfirmation(parts, "--create-goal");
    var createSimpleGoal = HasCliConfirmation(parts, "--create-simple-goal");
    var forceReclaim = HasCliConfirmation(parts, "--force-reclaim");
    var pipelineRequest = ResolveGoalIntakePipelineRequest(parts);
    if (createGoal && createSimpleGoal)
    {
        throw new ArgumentException("Use either --create-goal or --create-simple-goal, not both.");
    }
    if (createSimpleGoal)
    {
        RejectPipelineForSimpleGoal(pipelineRequest);
    }
    if (pipelineRequest is not null && !createGoal)
    {
        throw new ArgumentException("--pipeline requires --create-goal for backlog intake.");
    }

    var sourceBacklogCoverage = ResolveSourceBacklogCoverage(
        parts,
        sourceLinkDeclared: createGoal || createSimpleGoal);

    // Batch submission (a3f6b536): multiple positional filters each create one goal in a single command.
    // Single-filter behaviour below is unchanged; batch only engages with 2+ filters and a create flag.
    var batchFilters = GetBacklogIntakeFilters(context, parts);
    if (batchFilters.Count > 1 && (createGoal || createSimpleGoal))
    {
        var batchPlans = new List<(string Filter, BacklogIntakePlan Plan, GoalObjectivePlan? GoalPlan)>();
        foreach (var filter in batchFilters)
        {
            var itemPlan = BacklogIntakePlanner.Build(context.Workspace.BacklogStorePath, filter, 2);
            ThrowIfAmbiguousBacklogIntakeMatch(filter, itemPlan);
            GoalObjectivePlan? goalPlan = null;
            if (createGoal && itemPlan.Items.Count == 1)
            {
                goalPlan = BuildGoalObjectivePlan(
                    context,
                    itemPlan.Items.Single().SuggestedObjective,
                    simple: false,
                    pipelineRequest);
                GoalObjectivePlanner.ThrowIfBlocked(goalPlan);
                GoalLifecycleCommands.EnsureRequestedPipelineCanBeSatisfied(goalPlan, context.Agents);
            }

            batchPlans.Add((filter, itemPlan, goalPlan));
        }

        var created = 0;
        var matched = 0;
        foreach (var (filter, itemPlan, batchGoalPlan) in batchPlans)
        {
            if (itemPlan.Items.Count == 0)
            {
                Console.WriteLine($"No backlog item matched '{filter}'; skipping.");
                continue;
            }

            var batchItem = itemPlan.Items.Single();
            matched++;
            var batchBacklogItem = new BacklogStore(context.Workspace.BacklogStorePath)
                .GetByExactIdAsync(batchItem.Id).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"Backlog item '{batchItem.Id}' disappeared during intake.");
            if (TryReuseBacklogIntakeGoal(context, batchItem, out var reusedBatchGoal) ||
                TryReuseBacklogIntakeRecord(context, batchItem, out reusedBatchGoal))
            {
                if (reusedBatchGoal is not null)
                {
                    context.CurrentGoal = reusedBatchGoal;
                    Console.WriteLine($"Backlog slice '{batchItem.Heading}' already has goal {reusedBatchGoal.Id.Value[..8]}; no new goal created.");
                }
                continue;
            }
            ValidateBacklogPromotionPrerequisites(context, batchBacklogItem);

            var batchReservation = ReserveBacklogIntake(context, batchItem, forceReclaim);
            if (batchReservation.Kind != BacklogIntakeReservationKind.Acquired)
            {
                PrintBacklogIntakeRecord(batchReservation.Record, context.Kernel);
                continue;
            }

            var batchGoal = createSimpleGoal
                ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, context.Agents, batchItem.SuggestedObjective, context.Workspace, context.Providers, context.EventWriter)
                : GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, context.Agents, batchGoalPlan!, context.Workspace, context.Providers, context.EventWriter);

            if (!string.IsNullOrEmpty(batchItem.Id))
            {
                context.Kernel.SetGoalSourceBacklogItemLink(batchGoal.Id, batchItem.Id, sourceBacklogCoverage!.Value);
                ApplyBacklogPromotionDependencies(context, batchGoal, batchBacklogItem);
            }

            context.CurrentGoal = batchGoal;
            PersistBacklogIntakeGoal(context, batchItem, batchGoal);
            created++;
            Console.WriteLine(createSimpleGoal
                ? $"Created simple goal from backlog slice '{batchItem.Heading}'."
                : $"Created goal from backlog slice '{batchItem.Heading}'.");
            if (batchGoalPlan is not null)
            {
                ConsoleViews.PrintGoalObjectivePlan(batchGoalPlan);
            }
        }

        if (matched == 0)
        {
            throw new InvalidOperationException("No backlog items matched the requested filters.");
        }

        Console.WriteLine($"Created {created} goal(s) from {batchFilters.Count} requested backlog slice(s).");
        return created > 0;
    }

    var headingFilter = ResolveBacklogIntakeFilter(context, parts);
    var plan = BacklogIntakePlanner.Build(
        context.Workspace.BacklogStorePath,
        string.IsNullOrWhiteSpace(headingFilter) ? null : headingFilter,
        createGoal || createSimpleGoal ? 2 : 5);
    if (createGoal || createSimpleGoal)
    {
        ThrowIfAmbiguousBacklogIntakeMatch(headingFilter, plan);
    }

    if (plan.Items.Count == 0)
    {
        if (!string.IsNullOrWhiteSpace(headingFilter))
        {
            var doneItem = new BacklogStore(context.Workspace.BacklogStorePath)
                .GetByExactIdAsync(BacklogStore.SlugId(headingFilter)).GetAwaiter().GetResult();
            if (doneItem is { Status: BacklogItemStatus.Done })
            {
                Console.WriteLine($"Backlog item '{doneItem.Title}' is already Done; no goal created.");
                return false;
            }
        }

        throw new InvalidOperationException("No backlog items matched the requested filter.");
    }

    ConsoleViews.PrintBacklogIntakePlan(plan);
    if (!createGoal && !createSimpleGoal)
    {
        return false;
    }

    var item = plan.Items.Single();
    GoalObjectivePlan? goalObjectivePlan = null;
    if (createGoal)
    {
        goalObjectivePlan = BuildGoalObjectivePlan(
            context,
            item.SuggestedObjective,
            simple: false,
            pipelineRequest);
        GoalObjectivePlanner.ThrowIfBlocked(goalObjectivePlan);
        GoalLifecycleCommands.EnsureRequestedPipelineCanBeSatisfied(goalObjectivePlan, context.Agents);
    }
    var backlogItemId = item.Id;
    var sourceBacklogItem = new BacklogStore(context.Workspace.BacklogStorePath)
        .GetByExactIdAsync(backlogItemId).GetAwaiter().GetResult()
        ?? throw new InvalidOperationException($"Backlog item '{backlogItemId}' disappeared during intake.");
    if (TryReuseBacklogIntakeGoal(context, item, out var existingGoal) ||
        TryReuseBacklogIntakeRecord(context, item, out existingGoal))
    {
        if (existingGoal is not null)
        {
            context.CurrentGoal = existingGoal;
            Console.WriteLine($"Backlog slice already has goal {existingGoal.Id.Value[..8]}; no new goal created.");
            ConsoleViews.PrintGoal(existingGoal);
        }
        return false;
    }
    ValidateBacklogPromotionPrerequisites(context, sourceBacklogItem);

    var reservation = ReserveBacklogIntake(context, item, forceReclaim);
    if (reservation.Kind != BacklogIntakeReservationKind.Acquired)
    {
        PrintBacklogIntakeRecord(reservation.Record, context.Kernel);
        return false;
    }

    context.CurrentGoal = createSimpleGoal
        ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, context.Agents, item.SuggestedObjective, context.Workspace, context.Providers, context.EventWriter)
        : GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, context.Agents, goalObjectivePlan!, context.Workspace, context.Providers, context.EventWriter);

    if (!string.IsNullOrEmpty(backlogItemId))
    {
        context.Kernel.SetGoalSourceBacklogItemLink(context.CurrentGoal.Id, backlogItemId, sourceBacklogCoverage!.Value);
        ApplyBacklogPromotionDependencies(context, context.CurrentGoal, sourceBacklogItem);
    }

    PersistBacklogIntakeGoal(context, item, context.CurrentGoal);
    Console.WriteLine(createSimpleGoal ? "Created simple goal from backlog slice." : "Created goal from backlog slice.");
    if (goalObjectivePlan is not null)
    {
        ConsoleViews.PrintGoalObjectivePlan(goalObjectivePlan);
    }
    ConsoleViews.PrintGoal(context.CurrentGoal);
    return true;
}

private static void ThrowIfAmbiguousBacklogIntakeMatch(string? filter, BacklogIntakePlan plan)
{
    if (plan.Items.Count <= 1)
    {
        return;
    }

    var label = string.IsNullOrWhiteSpace(filter) ? "<empty>" : filter;
    var matches = string.Join(Environment.NewLine, plan.Items.Select(item => $"  {item.Id} - {item.Heading}"));
    throw new InvalidOperationException($"Backlog filter '{label}' matched multiple items; narrow the filter or use an exact id:{Environment.NewLine}{matches}");
}

private static bool TryReuseBacklogIntakeGoal(
    CliExecutionContext context,
    BacklogIntakeItem item,
    [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Goal? existingGoal)
{
    existingGoal = string.IsNullOrWhiteSpace(item.Id)
        ? null
        : context.Kernel.FindGoalBySourceBacklogItemId(item.Id);
    return existingGoal is not null;
}

private static bool TryReuseBacklogIntakeRecord(
    CliExecutionContext context,
    BacklogIntakeItem item,
    [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Goal? existingGoal)
{
    existingGoal = null;
    if (string.IsNullOrWhiteSpace(item.Id))
        return false;

    var record = new BacklogIntakeRecordStore(context.Workspace.SqliteStatePath).Get(item.Id);
    if (record is null || string.IsNullOrWhiteSpace(record.GoalId))
        return false;

    existingGoal = context.Kernel.Goals.FirstOrDefault(goal =>
        goal.Id.Value.Equals(record.GoalId, StringComparison.Ordinal));
    return existingGoal is not null;
}

private static BacklogIntakeReservation ReserveBacklogIntake(
    CliExecutionContext context,
    BacklogIntakeItem item,
    bool forceReclaim)
{
    return new BacklogIntakeRecordStore(context.Workspace.SqliteStatePath)
        .Reserve(item.Id, item.Heading, forceReclaim);
}

private static void PersistBacklogIntakeGoal(CliExecutionContext context, BacklogIntakeItem item, Goal goal)
{
    if (string.IsNullOrWhiteSpace(item.Id))
        return;

    context.PersistCheckpoint(context.Kernel);
    new BacklogIntakeRecordStore(context.Workspace.SqliteStatePath).MarkGoalCreated(item.Id, goal.Id.Value);
}

private static void PrintBacklogIntakeRecord(BacklogIntakeRecord record, AgentOrchestratorKernel kernel)
{
    Console.WriteLine($"Backlog intake for source {record.SourceBacklogItemId} is {record.Status}; no new goal created.");
    if (!string.IsNullOrWhiteSpace(record.GoalId))
    {
        Console.WriteLine($"Goal: {record.GoalId[..Math.Min(8, record.GoalId.Length)]}");
        var goal = kernel.Goals.FirstOrDefault(candidate => candidate.Id.Value.Equals(record.GoalId, StringComparison.Ordinal));
        if (goal is not null)
            ConsoleViews.PrintGoal(goal);
        Console.WriteLine($"Recommended next command: next {record.GoalId[..Math.Min(8, record.GoalId.Length)]} --full");
        return;
    }

    Console.WriteLine($"Started: {record.StartedAt:O}");
    Console.WriteLine($"Last heartbeat: {record.LastHeartbeatAt:O}");
    if (record.OwnerProcessId is int ownerPid)
        Console.WriteLine($"Owner PID: {ownerPid}");
    if (!string.IsNullOrWhiteSpace(record.StdoutPath))
        Console.WriteLine($"Log: {record.StdoutPath}");
    if (!string.IsNullOrWhiteSpace(record.StderrPath))
        Console.WriteLine($"Error log: {record.StderrPath}");
    Console.WriteLine($"Recommended next command: retry after the owner exits, or rerun backlog-intake \"{record.Heading}\" --force-reclaim after confirming it is stale.");
}
}

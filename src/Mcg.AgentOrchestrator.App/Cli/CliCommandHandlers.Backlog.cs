using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
private static bool? TryExecuteBacklogCommand(string command, IReadOnlyList<string> parts, CliExecutionContext context)
{
    switch (command)
    {
        case "backlog-list":
        {
            var limit = ParseOptionalLimit(parts);
            var status = GetFlagValue(parts, "--status");
            var text = GetFlagValue(parts, "--text");
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var filtersByListingStatus = IsBacklogListingStatus(status);
            var items = ApplyBacklogListFilters(
                store.ListAsync(includeAll: true).GetAwaiter().GetResult(),
                filtersByListingStatus ? null : status,
                text,
                filtersByListingStatus ? null : limit);
            if (filtersByListingStatus)
            {
                items = items
                    .Where(item => RenderBacklogListingStatus(item, FindAuthoritativeSourceGoalForRead(context, item.Id, out _))
                        .Equals(status, StringComparison.OrdinalIgnoreCase))
                    .Take(limit ?? int.MaxValue)
                    .ToArray();
            }

            foreach (var item in items)
            {
                var claim = ResolveSourceBacklogClaimForRead(context, item.Id, out var ambiguity);
                var linkedGoal = claim is null
                    ? null
                    : context.Kernel.Goals.FirstOrDefault(goal => goal.Id.Value == claim.OwnerGoalId);
                var goalId = ambiguity is null ? linkedGoal?.Id.Value ?? "-" : "legacy-owner-ambiguous";
                Console.WriteLine($"{RenderBacklogListTag(item)}{RenderBacklogTimestampSuffix(item)}{RenderBacklogListSuffix(item)}{RenderBacklogReadinessSuffix(item, context, store)} {item.Id} | status={RenderBacklogListingStatus(item, linkedGoal)} | goal={goalId} | title={item.Title}");
            }
            Console.WriteLine($"Backlog list: {items.Count} item(s) from backlog store");
            return false;
        }

        case "backlog-similar":
        {
            var idFlagPresent = parts.Any(part =>
                part.Equals("--id", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("--id=", StringComparison.OrdinalIgnoreCase));
            var idPrefix = GetFlagValue(parts, "--id");
            var freeText = GetBacklogSimilarFreeText(parts);
            if (idFlagPresent && (string.IsNullOrWhiteSpace(idPrefix) || idPrefix.StartsWith("--", StringComparison.Ordinal)))
                throw new ArgumentException("--id requires a backlog id prefix.");
            if (idFlagPresent && freeText.Length > 0)
                throw new ArgumentException("Provide either free text or --id, not both.");
            if (!idFlagPresent && freeText.Length == 0)
                throw new ArgumentException(CliCommandHelp.BacklogSimilarUsage);

            var corpus = LoadBacklogSimilarityCorpus(context);
            string queryText;
            string? excludeId = null;
            if (idFlagPresent)
            {
                var matchingItems = corpus
                    .Where(document => document.Kind.Equals("backlog", StringComparison.OrdinalIgnoreCase) &&
                        document.Id.StartsWith(idPrefix!, StringComparison.OrdinalIgnoreCase))
                    .Take(2)
                    .ToArray();
                if (matchingItems.Length == 0)
                    throw new InvalidOperationException($"No backlog item found with id prefix '{idPrefix}'.");
                if (matchingItems.Length > 1)
                    throw new InvalidOperationException($"Ambiguous id prefix '{idPrefix}' matches multiple items.");
                queryText = matchingItems[0].Text;
                excludeId = matchingItems[0].Id;
            }
            else
            {
                queryText = freeText;
            }

            var hits = BacklogSimilaritySearch.Search(
                corpus,
                queryText,
                ParseOptionalLimit(parts) ?? 10,
                parts.Any(part => part.Equals("--excerpt", StringComparison.OrdinalIgnoreCase)),
                GetFlagValue(parts, "--status"),
                excludeId);
            foreach (var hit in hits)
                Console.WriteLine(RenderSimilarityHit(hit));
            Console.WriteLine($"Backlog similar: {hits.Count} result(s)");
            return false;
        }

        case "backlog-triage":
        {
            var limit = ParseOptionalLimit(parts) ?? 5;
            var staleDays = ParseOptionalNonNegativeInt(parts, "--stale-days") ?? 30;
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var items = store.ListAsync(includeAll: true).GetAwaiter().GetResult();
            Console.Write(RenderBacklogTriage(items, context.Kernel.Goals, limit, staleDays, DateTimeOffset.UtcNow));
            return false;
        }

        case "backlog-add":
        {
            var requestedEpic = EpicAtCreation.ResolveRequested(context.Workspace,
                EpicAtCreation.HasArgument(parts) ? GetFlagValue(parts, "--epic") ?? "" : null);
            var titleFlagCount = parts.Count(part => part.Equals("--title", StringComparison.OrdinalIgnoreCase));
            var hasPositionalTitle = parts.Count > 1 && !parts[1].StartsWith("--", StringComparison.Ordinal);
            if (titleFlagCount > 1)
                throw new ArgumentException("Provide --title only once.");
            if (titleFlagCount == 1 && hasPositionalTitle)
                throw new ArgumentException("Provide the backlog title either positionally or with --title, not both.");

            var flaggedTitle = titleFlagCount == 1 ? GetFlagValue(parts, "--title") : null;
            if (titleFlagCount == 1 &&
                (string.IsNullOrWhiteSpace(flaggedTitle) || flaggedTitle.StartsWith("--", StringComparison.Ordinal)))
            {
                throw new ArgumentException("--title requires a non-empty title.");
            }
            if (titleFlagCount == 0 && !hasPositionalTitle)
                throw new ArgumentException(CliCommandHelp.BacklogAddUsage);

            var bodyParts = titleFlagCount == 1 ? RemoveFlagWithValue(parts, "--title") : parts;
            if (titleFlagCount == 1 && bodyParts.Count > 1 && !bodyParts[1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("When using --title, provide the body with --text-file or --body-file.");
            }

            var title = flaggedTitle ?? parts[1];
            var body = ResolveTextArgumentOrDefault(
                bodyParts,
                inlineIndex: titleFlagCount == 1 ? 1 : 2,
                defaultValue: "",
                "--body-file",
                "--text-file") ?? "";
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var dependencies = GetFlagValues(parts, "--depends-on")
                .Select(prefix => ResolveBacklogDependencyTarget(context, store, prefix))
                .ToArray();
            var item = dependencies.Length == 0
                ? store.AddAsync(title, body).GetAwaiter().GetResult()
                : store.AddWithDependenciesAsync(
                    title,
                    body,
                    dependencies,
                    goalExists: id => context.Kernel.Goals.Any(goal => goal.Id.Value == id))
                    .GetAwaiter()
                    .GetResult();
            Console.WriteLine($"Added: [{item.Id}] {item.Title}");
            EpicAtCreation.AssignAfterCommit(context.Workspace, PortfolioMemberKind.BacklogItem, item.Id, requestedEpic?.Id);
            if (!parts.Any(part => part.Equals("--no-similar", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var hits = SearchBacklogSimilarity(
                        context,
                        item.Title + "\n" + item.Body,
                        limit: 3,
                        includeExcerpt: false,
                        statusFilter: null,
                        excludeId: item.Id);
                    if (hits.Count > 0)
                    {
                        Console.WriteLine("Similar:");
                        foreach (var hit in hits)
                            Console.WriteLine(RenderSimilarityHit(hit));
                    }
                }
                catch (Exception)
                {
                    Console.WriteLine("Warning: similarity search unavailable; item was added.");
                }
            }
            return false;
        }

        case "backlog-update":
        {
            if (parts.Count < 2)
                throw new ArgumentException(CliCommandHelp.BacklogUpdateUsage);
            var update = ParseBacklogItemUpdate(parts);
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var item = ResolveBacklogItemByPrefix(store, parts[1]);
            var updated = store.UpdateAsync(item.Id, update).GetAwaiter().GetResult();
            Console.WriteLine($"Updated: [{updated.Id}] {updated.Title}");
            return false;
        }

        case "backlog-show":
        {
            CliArgumentParser.RequirePartCount(parts, 2, "backlog-show <id-prefix>");
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var item = store.GetByIdPrefixAsync(parts[1]).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"No backlog item found with id prefix '{parts[1]}'.");
            Console.WriteLine($"Id:      {item.Id}");
            Console.WriteLine($"Title:   {item.Title}");
            Console.WriteLine($"Status:  {item.Status}");
            Console.WriteLine($"Created: {item.CreatedAt:O}");
            Console.WriteLine($"Updated: {item.UpdatedAt:O}");
            if (item.SourceGoalId is not null)
                Console.WriteLine($"Goal:    {item.SourceGoalId}");
            var claimStore = new SourceBacklogClaimStore(context.Workspace.SqliteStatePath);
            var authoritativeClaim = ResolveSourceBacklogClaimForRead(context, item.Id, out var ambiguity);
            Console.WriteLine($"Owner:   {(ambiguity is null ? authoritativeClaim?.OwnerGoalId ?? "-" : "legacy-owner-ambiguous")}");
            Console.WriteLine($"Version: {authoritativeClaim?.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}");
            Console.WriteLine($"Coverage:{(authoritativeClaim is null ? " -" : $" {authoritativeClaim.Coverage.ToString().ToLowerInvariant()}")}");
            if (ambiguity is not null)
                Console.WriteLine($"Claim:   reason=legacy-owner-ambiguous linkedGoals={string.Join(',', ambiguity.LinkedGoalIds)}");
            var linkedGoals = context.Kernel.Goals
                .Where(goal => string.Equals(goal.SourceBacklogItemId, item.Id, StringComparison.Ordinal))
                .OrderBy(goal => goal.Id.Value, StringComparer.Ordinal)
                .ToList();
            if (linkedGoals.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Linked goals:");
                foreach (var goal in linkedGoals)
                {
                    var authority = ambiguity is not null
                        ? "ambiguous"
                        : authoritativeClaim?.OwnerGoalId == goal.Id.Value ? "authoritative" : "historical";
                    Console.WriteLine($"- {goal.Id.Value[..8]} status={goal.Status} authority={authority} landing={ResolveBacklogShowGoalLandingState(context, goal)}");
                }
            }
            var lineage = claimStore.ListLineage(item.Id);
            if (lineage.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Replacement lineage:");
                foreach (var replacement in lineage)
                    Console.WriteLine($"- predecessor={replacement.PredecessorGoalId} successor={replacement.SuccessorGoalId} request={replacement.RequestId:D} at={replacement.ReplacedAt:O}");
            }
            if (item.Dependencies.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Dependencies:");
                foreach (var dependency in item.Dependencies)
                    Console.WriteLine($"- {dependency.PrerequisiteId} state={RenderBacklogDependencyState(context, store, dependency)}");
            }
            if (item.Dependents.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Dependents:");
                foreach (var dependent in item.Dependents)
                    Console.WriteLine($"- {dependent.DependentId}");
            }
            if (!string.IsNullOrWhiteSpace(item.Body))
            {
                Console.WriteLine();
                Console.WriteLine(item.Body);
            }
            if (item.Notes.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Notes:");
                foreach (var note in item.Notes)
                {
                    Console.WriteLine($"- {note.CreatedAt:O}");
                    Console.WriteLine(note.Text);
                }
            }
            return false;
        }

        case "backlog-annotate":
        {
            CliArgumentParser.RequirePartCount(parts, 2, "backlog-annotate <id-prefix> <note> | backlog-annotate <id-prefix> --text-file <path>");
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var item = store.GetByIdPrefixAsync(parts[1]).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"No backlog item found with id prefix '{parts[1]}'.");
            var note = ResolveTextArgumentOrDefault(parts, inlineIndex: 2, defaultValue: null, "--text-file")
                ?? throw new ArgumentException("Usage: backlog-annotate <id-prefix> <note> | backlog-annotate <id-prefix> --text-file <path>");
            var annotated = store.AppendNoteAsync(item.Id, note).GetAwaiter().GetResult();
            Console.WriteLine($"Annotated: [{annotated.Id}] {annotated.Title}");
            return false;
        }

        case "backlog-close":
        {
            CliArgumentParser.RequirePartCount(parts, 2, "backlog-close <id-prefix> [reason] | backlog-close <id-prefix> --reason-file <path>");
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var item = ResolveBacklogItemByPrefix(store, parts[1]);
            var reason = ResolveTextArgumentOrDefault(parts, inlineIndex: 2, defaultValue: null, "--reason-file", "--text-file");
            var closed = store.CloseAsync(item.Id, reason).GetAwaiter().GetResult();
            Console.WriteLine($"Closed: [{closed.Id}] {closed.Title}");
            return false;
        }

        case "backlog-supersede":
        {
            CliArgumentParser.RequirePartCount(parts, 3, "backlog-supersede <old-id-prefix> <new-id-prefix>");
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var oldItem = ResolveBacklogItemByPrefix(store, parts[1]);
            var newItem = ResolveBacklogItemByPrefix(store, parts[2]);
            var superseded = store.SupersedeAsync(oldItem.Id, newItem.Id).GetAwaiter().GetResult();
            Console.WriteLine($"Superseded: [{superseded.Id}] -> {newItem.Id[..Math.Min(8, newItem.Id.Length)]}");
            return false;
        }

        case "backlog-unsupersede":
        {
            CliArgumentParser.RequirePartCount(parts, 2, "backlog-unsupersede <id-prefix>");
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var item = ResolveBacklogItemByPrefix(store, parts[1]);
            var result = store.UnsupersedeAsync(item.Id).GetAwaiter().GetResult();
            Console.WriteLine(result.Changed
                ? $"Unsuperseded: [{result.Item.Id}] {result.Item.Title}"
                : $"Not superseded: [{result.Item.Id}] {result.Item.Title}");
            return false;
        }

        case "backlog-link":
        {
            CliArgumentParser.RequirePartCount(parts, 3, "backlog-link <canonical-id-prefix> <duplicate-id-prefix> [--related]");
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var first = ResolveBacklogItemByPrefix(store, parts[1]);
            var second = ResolveBacklogItemByPrefix(store, parts[2]);
            var kind = HasCliConfirmation(parts, "--related") ? BacklogLinkKind.Related : BacklogLinkKind.Duplicate;
            var link = store.LinkAsync(first.Id, second.Id, kind).GetAwaiter().GetResult();
            Console.WriteLine($"{(kind == BacklogLinkKind.Duplicate ? "Linked duplicate" : "Linked related")}: [{link.Item1Id[..Math.Min(8, link.Item1Id.Length)]}] <-> [{link.Item2Id[..Math.Min(8, link.Item2Id.Length)]}]");
            return false;
        }

        case "backlog-depends":
        {
            CliArgumentParser.RequirePartCount(parts, 3, CliCommandHelp.BacklogDependsUsage);
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var item = ResolveBacklogItemByPrefix(store, parts[1]);
            var on = GetFlagValue(parts, "--on");
            var remove = GetFlagValue(parts, "--remove");
            var clear = HasCliConfirmation(parts, "--clear");
            if ((on is not null ? 1 : 0) + (remove is not null ? 1 : 0) + (clear ? 1 : 0) != 1)
                throw new ArgumentException(CliCommandHelp.BacklogDependsUsage);

            if (clear)
            {
                store.ClearDependenciesAsync(item.Id).GetAwaiter().GetResult();
                Console.WriteLine($"Dependencies cleared: {ShortBacklogId(item.Id)}");
                return false;
            }

            var prefix = on ?? remove!;
            var target = ResolveBacklogDependencyTarget(context, store, prefix);
            if (on is not null)
            {
                store.AddDependencyAsync(
                    item.Id,
                    target,
                    goalExists: id => context.Kernel.Goals.Any(goal => goal.Id.Value == id))
                    .GetAwaiter()
                    .GetResult();
                Console.WriteLine($"Dependency set: {ShortBacklogId(item.Id)} depends on {ShortBacklogId(target.Id)}");
            }
            else
            {
                store.RemoveDependencyAsync(item.Id, target.Id).GetAwaiter().GetResult();
                Console.WriteLine($"Dependency removed: {ShortBacklogId(item.Id)} no longer depends on {ShortBacklogId(target.Id)}");
            }
            return false;
        }

        case "backlog-reopen":
        {
            CliArgumentParser.RequirePartCount(parts, 2, "backlog-reopen <id-prefix> [reason]");
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var item = store.GetByIdPrefixAsync(parts[1]).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"No backlog item found with id prefix '{parts[1]}'.");
            var reason = parts.Count > 2 ? parts[2] : null;
            var reopened = store.ReopenAsync(item.Id, reason).GetAwaiter().GetResult();
            Console.WriteLine($"Reopened: [{reopened.Id}] {reopened.Title}");
            return false;
        }

        case "backlog-view":
        {
            var store = new BacklogStore(context.Workspace.BacklogStorePath);
            var all = store.ListAsync(includeAll: true).GetAwaiter().GetResult();
            Console.Write(RenderBacklogMarkdown(all));
            return false;
        }

        default:
            return null;
    }
}

private static Goal? FindAuthoritativeSourceGoalForRead(
    CliExecutionContext context,
    string backlogItemId,
    out LegacySourceBacklogOwnerAmbiguousException? ambiguity)
{
    var claim = ResolveSourceBacklogClaimForRead(context, backlogItemId, out ambiguity);
    return claim is null
        ? null
        : context.Kernel.Goals.FirstOrDefault(goal => goal.Id.Value == claim.OwnerGoalId);
}

private static SourceBacklogClaimSnapshot? ResolveSourceBacklogClaimForRead(
    CliExecutionContext context,
    string backlogItemId,
    out LegacySourceBacklogOwnerAmbiguousException? ambiguity)
{
    try
    {
        ambiguity = null;
        return new SourceBacklogClaimStore(context.Workspace.SqliteStatePath)
            .ResolveClaim(context.Kernel, backlogItemId);
    }
    catch (LegacySourceBacklogOwnerAmbiguousException ex)
    {
        ambiguity = ex;
        return null;
    }
}

private static string ResolveBacklogShowGoalLandingState(CliExecutionContext context, Goal goal)
{
    var journal = GoalOperationJournal.Read(context.Workspace.ExecutionDirectory, goal.Id);
    var workspaceExists = context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id) is not null;
    var isMerged = GoalOperationJournal.HasCompletedLandingEvidence(journal);
    var isRecorded = journal.LatestByOperation.Any(entry =>
        entry.Operation.Equals("conductor:record", StringComparison.OrdinalIgnoreCase) &&
        entry.Status == GoalOperationStatus.Completed);
    var isCleanedUp = journal.LatestByOperation.Any(entry =>
        entry.Operation.Equals("workspace:remove", StringComparison.OrdinalIgnoreCase) &&
        entry.Status == GoalOperationStatus.Completed);
    return GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(workspaceExists, IsMerged: isMerged, IsRecorded: isRecorded, IsCleanedUp: isCleanedUp)).ToString();
}

private static BacklogItem ResolveBacklogItemByPrefix(BacklogStore store, string prefix) =>
    store.GetByIdPrefixAsync(prefix).GetAwaiter().GetResult()
        ?? throw new InvalidOperationException($"No backlog item found with id prefix '{prefix}'.");

private static BacklogDependencyTarget ResolveBacklogDependencyTarget(
    CliExecutionContext context,
    BacklogStore store,
    string prefix)
{
    var backlogMatches = store.FindByIdPrefixAsync(prefix).GetAwaiter().GetResult();
    var goalMatches = context.Kernel.Goals
        .Where(goal => goal.Id.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .OrderBy(goal => goal.Id.Value, StringComparer.Ordinal)
        .ToArray();
    var candidateCount = backlogMatches.Count + goalMatches.Length;
    if (candidateCount == 0)
        throw new InvalidOperationException($"No backlog item or goal found with id prefix '{prefix}'.");
    if (candidateCount > 1)
    {
        var candidates = backlogMatches.Select(item => $"backlog:{item.Id}")
            .Concat(goalMatches.Select(goal => $"goal:{goal.Id.Value}"));
        throw new InvalidOperationException(
            $"Ambiguous dependency prefix '{prefix}' matches: {string.Join(", ", candidates)}.");
    }

    var backlog = backlogMatches.SingleOrDefault();
    return backlog is not null
        ? new BacklogDependencyTarget(backlog.Id, BacklogDependencyTargetKind.Backlog)
        : new BacklogDependencyTarget(goalMatches[0].Id.Value, BacklogDependencyTargetKind.Goal);
}

private static string RenderBacklogDependencyState(
    CliExecutionContext context,
    BacklogStore store,
    BacklogDependency dependency)
{
    if (dependency.TargetKind == BacklogDependencyTargetKind.Goal)
    {
        var goal = context.Kernel.Goals.FirstOrDefault(candidate => candidate.Id.Value == dependency.PrerequisiteId);
        return goal is null
            ? "missing"
            : $"{goal.Status}/{ResolveBacklogShowGoalLandingState(context, goal)}";
    }

    var item = store.GetByExactIdAsync(dependency.PrerequisiteId).GetAwaiter().GetResult();
    if (item is null)
        return "missing";
    var promotedGoal = FindAuthoritativeSourceGoalForRead(context, item.Id, out var ambiguity);
    if (ambiguity is not null)
        return $"{item.Status}/legacy-owner-ambiguous";
    return promotedGoal is null
        ? item.Status.ToString()
        : $"{item.Status}/{promotedGoal.Status}/{ResolveBacklogShowGoalLandingState(context, promotedGoal)}";
}

private static string RenderBacklogReadinessSuffix(
    BacklogItem item,
    CliExecutionContext context,
    BacklogStore store) =>
    BacklogDependencyReadiness.Evaluate(item,
        id => context.Kernel.Goals.FirstOrDefault(goal => goal.Id.Value == id),
        id => store.GetByExactIdAsync(id).GetAwaiter().GetResult(),
        id =>
        {
            var goal = FindAuthoritativeSourceGoalForRead(context, id, out var ambiguity);
            return new BacklogPrerequisiteOwner(goal, ambiguity is not null);
        },
        goal => ResolveBacklogShowGoalLandingState(context, goal)).Suffix;

private static BacklogItemUpdate ParseBacklogItemUpdate(IReadOnlyList<string> parts)
{
    var values = ReadBacklogUpdateValues(parts);
    var title = values.GetValueOrDefault("--title");
    var description = values.GetValueOrDefault("--description");
    var priority = values.GetValueOrDefault("--priority");
    var tags = values.GetValueOrDefault("--tags");
    description = ResolveBacklogUpdateDescription(parts, values, description);
    BacklogItemStatus? status = null;
    if (values.GetValueOrDefault("--status") is { } statusValue)
    {
        if (!Enum.TryParse<BacklogItemStatus>(statusValue, ignoreCase: true, out var parsed))
            throw new ArgumentException("--status must be one of: open, done, superseded.");
        status = parsed;
    }

    if (title is null &&
        description is null &&
        priority is null &&
        tags is null &&
        status is null)
    {
        throw new ArgumentException(CliCommandHelp.BacklogUpdateUsage);
    }

    return new BacklogItemUpdate(title, description, priority, tags, status);
}

internal static string RenderBacklogListTag(BacklogItem item)
{
    if (item.SupersededBy is not null)
        return $"[Superseded→{ShortBacklogId(item.SupersededBy)}]";

    var duplicate = item.Links.FirstOrDefault(link =>
        link.Kind == BacklogLinkKind.Duplicate &&
        string.Equals(link.DuplicateId, item.Id, StringComparison.Ordinal));
    if (duplicate?.CanonicalId is not null)
        return $"[Dup→{ShortBacklogId(duplicate.CanonicalId)}]";

    return $"[{item.Status}]";
}

internal static string RenderBacklogTimestampSuffix(BacklogItem item) =>
    FormattableString.Invariant($" [created={item.CreatedAt.UtcDateTime:yyyy-MM-dd}] [updated={item.UpdatedAt.UtcDateTime:yyyy-MM-dd}]");

internal static string RenderBacklogListSuffix(BacklogItem item)
{
    var related = item.Links
        .Where(link => link.Kind == BacklogLinkKind.Related)
        .Select(link => $"[Related→{ShortBacklogId(link.OtherId(item.Id))}]")
        .ToArray();
    return related.Length == 0 ? "" : " " + string.Join(' ', related);
}

private static Goal? FindLinkedGoal(IReadOnlyDictionary<string, Goal> goalsByBacklogItem, string backlogItemId) =>
    goalsByBacklogItem.TryGetValue(backlogItemId, out var goal) ? goal : null;

private static bool IsBacklogListingStatus(string? status) =>
    status is not null &&
    (status.Equals("claimed", StringComparison.OrdinalIgnoreCase) ||
     status.Equals("closed", StringComparison.OrdinalIgnoreCase));

private static string RenderBacklogListingStatus(BacklogItem item, Goal? linkedGoal)
{
    if (item.Status != BacklogItemStatus.Open)
        return "closed";

    return linkedGoal is null ? "open" : "claimed";
}

internal static IReadOnlyList<BacklogItem> ApplyBacklogListFilters(
    IReadOnlyList<BacklogItem> items,
    string? status,
    string? text,
    int? limit)
{
    IEnumerable<BacklogItem> query = items;
    if (!string.IsNullOrWhiteSpace(status))
    {
        query = query.Where(item => item.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase));
    }

    if (!string.IsNullOrWhiteSpace(text))
    {
        query = query.Where(item =>
            item.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            item.Body.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    if (limit is { } cap)
    {
        query = query.Take(cap);
    }

    return query.ToArray();
}

internal static string RenderBacklogTriage(
    IReadOnlyList<BacklogItem> items,
    IReadOnlyCollection<Goal> goals,
    int limit,
    int staleDays,
    DateTimeOffset now)
{
    if (limit < 0)
        throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be non-negative.");
    if (staleDays < 0)
        throw new ArgumentOutOfRangeException(nameof(staleDays), "Stale days must be non-negative.");

    var openItems = items.Where(item => item.Status == BacklogItemStatus.Open).ToArray();
    var activeGoalByBacklogId = goals
        .Where(goal => goal.SourceBacklogItemId is not null && !IsTerminalGoalStatus(goal.Status))
        .GroupBy(goal => goal.SourceBacklogItemId!, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    var linkedOpenItems = openItems
        .Where(item => activeGoalByBacklogId.ContainsKey(item.Id))
        .ToArray();
    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"Backlog triage: open={openItems.Length} done={items.Count - openItems.Length} active-linked={linkedOpenItems.Length} limit={limit}");
    AppendTriageBucket(
        sb,
        $"Stale open (>{staleDays}d)",
        openItems
            .Where(item => (now - item.UpdatedAt).TotalDays > staleDays)
            .OrderBy(item => item.UpdatedAt),
        limit,
        activeGoalByBacklogId,
        now);
    AppendTriageBucket(
        sb,
        "Active-linked open",
        linkedOpenItems.OrderBy(item => item.UpdatedAt),
        limit,
        activeGoalByBacklogId,
        now);
    AppendTriageBucket(
        sb,
        "Blocked-looking open",
        openItems
            .Where(IsBlockedLooking)
            .OrderBy(item => item.UpdatedAt),
        limit,
        activeGoalByBacklogId,
        now);
    AppendTriageBucket(
        sb,
        "High-priority open",
        openItems
            .Where(IsHighPriorityLooking)
            .OrderBy(item => item.UpdatedAt),
        limit,
        activeGoalByBacklogId,
        now);
    AppendDuplicateBucket(sb, openItems, limit);
    return sb.ToString();
}

private static int? ParseOptionalLimit(IReadOnlyList<string> parts)
{
    return ParseOptionalNonNegativeInt(parts, "--limit");
}

private static string GetBacklogSimilarFreeText(IReadOnlyList<string> parts)
{
    var words = new List<string>();
    for (var index = 1; index < parts.Count; index++)
    {
        var part = parts[index];
        if (part.Equals("--excerpt", StringComparison.OrdinalIgnoreCase))
            continue;
        if (part.StartsWith("--id=", StringComparison.OrdinalIgnoreCase) ||
            part.StartsWith("--limit=", StringComparison.OrdinalIgnoreCase) ||
            part.StartsWith("--status=", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }
        if (part.Equals("--id", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("--limit", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("--status", StringComparison.OrdinalIgnoreCase))
        {
            index++;
            continue;
        }
        words.Add(part);
    }
    return string.Join(' ', words);
}

private static IReadOnlyList<SimilarityHit> SearchBacklogSimilarity(
    CliExecutionContext context,
    string queryText,
    int limit,
    bool includeExcerpt,
    string? statusFilter,
    string? excludeId)
{
    var corpus = LoadBacklogSimilarityCorpus(context);
    return BacklogSimilaritySearch.Search(corpus, queryText, limit, includeExcerpt, statusFilter, excludeId);
}

private static List<SimilarityDocument> LoadBacklogSimilarityCorpus(CliExecutionContext context)
{
    var corpus = BacklogSimilaritySearch.LoadBacklogDocumentsAsync(context.Workspace.BacklogStorePath)
        .GetAwaiter().GetResult().ToList();
    corpus.AddRange(context.Kernel.Goals
        .Where(goal => goal.Status == GoalStatus.Completed)
        .Select(goal => new SimilarityDocument(
            "goal",
            goal.Id.Value,
            goal.Status.ToString(),
            FirstNonEmptyLine(goal.Objective),
            goal.MetadataTerminatedAt ?? goal.MetadataCreatedAt ??
                goal.Timeline.OrderBy(item => item.OccurredAt).LastOrDefault()?.OccurredAt ?? DateTimeOffset.MinValue,
            goal.Objective)));
    return corpus;
}

private static string RenderSimilarityHit(SimilarityHit hit)
{
    var idPrefix = hit.Id[..Math.Min(8, hit.Id.Length)];
    var title = string.Join(' ', hit.Title
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    if (title.Length == 0)
        title = "-";
    var excerpt = hit.Excerpt is null ? "" : $" excerpt={hit.Excerpt}";
    return $"- kind={hit.Kind} id={idPrefix} status={hit.Status} title={title} updated={hit.UpdatedAt:yyyy-MM-dd} rank={hit.Rank.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)}{excerpt}";
}

private static string FirstNonEmptyLine(string text) =>
    text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault() ?? "-";

private static int? ParseOptionalNonNegativeInt(IReadOnlyList<string> parts, string flag)
{
    var value = GetFlagValue(parts, flag);
    if (value is null)
    {
        return null;
    }

    if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var limit) ||
        limit < 0)
    {
        throw new ArgumentException($"{flag} requires a non-negative integer value.");
    }

    return limit;
}

private static void AppendTriageBucket(
    System.Text.StringBuilder sb,
    string heading,
    IEnumerable<BacklogItem> items,
    int limit,
    IReadOnlyDictionary<string, Goal> activeGoalByBacklogId,
    DateTimeOffset now)
{
    var selected = items.Take(limit).ToArray();
    sb.AppendLine(heading + ":");
    if (selected.Length == 0)
    {
        sb.AppendLine("- none");
        return;
    }

    foreach (var item in selected)
    {
        var ageDays = Math.Max(0, (int)Math.Floor((now - item.UpdatedAt).TotalDays));
        var goalText = activeGoalByBacklogId.TryGetValue(item.Id, out var goal)
            ? $" goal={goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)]}:{goal.Status}"
            : "";
        sb.AppendLine($"- {ShortBacklogId(item)} age={ageDays}d{goalText} {item.Title}");
    }
}

private static void AppendDuplicateBucket(System.Text.StringBuilder sb, IReadOnlyList<BacklogItem> openItems, int limit)
{
    var duplicateGroups = openItems
        .GroupBy(item => DuplicateKey(item.Title), StringComparer.Ordinal)
        .Where(group => group.Key.Length > 0 && group.Count() > 1)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key, StringComparer.Ordinal)
        .Take(limit)
        .ToArray();

    sb.AppendLine("Duplicate-looking open:");
    if (duplicateGroups.Length == 0)
    {
        sb.AppendLine("- none");
        return;
    }

    foreach (var group in duplicateGroups)
    {
        var sample = string.Join(" | ", group.Take(3).Select(item => $"{ShortBacklogId(item)} {item.Title}"));
        sb.AppendLine($"- {group.Count()}x {group.Key}: {sample}");
    }
}

private static string ShortBacklogId(BacklogItem item) => ShortBacklogId(item.Id);

private static string ShortBacklogId(string id) => id[..Math.Min(8, id.Length)];

private static bool IsTerminalGoalStatus(GoalStatus status) =>
    status is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded;

private static bool IsBlockedLooking(BacklogItem item) =>
    ContainsAny(item.Title, "blocked", "blocker", "stuck", "waiting", "human input") ||
    ContainsAny(item.Body, "blocked", "blocker", "stuck", "waiting", "human input");

private static bool IsHighPriorityLooking(BacklogItem item) =>
    ContainsAny(item.Title, "p0", "p1", "urgent", "critical", "high-priority", "high priority") ||
    ContainsAny(item.Body, "p0", "p1", "urgent", "critical", "high-priority", "high priority");

private static bool ContainsAny(string text, params string[] needles) =>
    needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

private static string DuplicateKey(string title)
{
    var normalized = new string(title
        .ToLowerInvariant()
        .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
        .ToArray());
    return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

internal static string RenderBacklogMarkdown(IReadOnlyList<BacklogItem> items)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("# Backlog");
    sb.AppendLine();

    var openItems = items.Where(i => i.Status == BacklogItemStatus.Open).ToList();
    var doneItems = items.Where(i => i.Status == BacklogItemStatus.Done).ToList();

    foreach (var item in openItems)
    {
        sb.AppendLine($"## {item.Title}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(item.Body))
        {
            sb.AppendLine(item.Body);
            sb.AppendLine();
        }
    }

    if (doneItems.Count > 0)
    {
        sb.AppendLine("## Shipped (closed)");
        sb.AppendLine();
        foreach (var item in doneItems)
            sb.AppendLine($"- **{item.Title}** — {item.UpdatedAt:yyyy-MM-dd}");
    }

    return sb.ToString();
}
}

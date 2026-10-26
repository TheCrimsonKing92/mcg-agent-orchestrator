using System.Reflection;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: all state is in memory and every clock is fixed.
public sealed class BacklogTextViewTests
{
    private static readonly DateTimeOffset FixtureNow = new(2026, 7, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Triage_RendersEveryBucketForFixedFixture()
    {
        var (items, goals, activeGoalId) = CreateTriageFixture();

        var output = BacklogTextView.RenderBacklogTriage(items, goals, 5, 30, FixtureNow);

        Assert.Equal(new[]
        {
            "Backlog triage: open=8 done=1 active-linked=1 limit=5",
            "Stale open (>30d):",
            "- stale-01 age=45d Very stale open",
            "- linked-t age=40d Linked terminal work",
            "Active-linked open:",
            $"- linked-a age=1d goal={activeGoalId.Value[..8]}:Active Linked active work",
            "Blocked-looking open:",
            "- blocked1 age=3d Blocked on provider quota",
            "High-priority open:",
            "- urgent-1 age=4d P1 urgent gate fix",
            "Duplicate-looking open:",
            "- 2x fix the build: dup-0001 Fix the build! | dup-0002 fix-the build"
        }, output.Split(Environment.NewLine).SkipLast(1).ToArray());
    }

    [Fact]
    public void Triage_HonorsLimitAndRejectsNegativeArguments()
    {
        var (items, goals, activeGoalId) = CreateTriageFixture();

        var emptyOutput = BacklogTextView.RenderBacklogTriage(items, goals, 0, 30, FixtureNow);

        Assert.Equal(new[]
        {
            "Backlog triage: open=8 done=1 active-linked=1 limit=0",
            "Stale open (>30d):",
            "- none",
            "Active-linked open:",
            "- none",
            "Blocked-looking open:",
            "- none",
            "High-priority open:",
            "- none",
            "Duplicate-looking open:",
            "- none"
        }, emptyOutput.Split(Environment.NewLine).SkipLast(1).ToArray());
        Assert.Equal("limit", Assert.Throws<ArgumentOutOfRangeException>(() =>
            BacklogTextView.RenderBacklogTriage(items, goals, -1, 30, FixtureNow)).ParamName);
        Assert.Equal("staleDays", Assert.Throws<ArgumentOutOfRangeException>(() =>
            BacklogTextView.RenderBacklogTriage(items, goals, 5, -1, FixtureNow)).ParamName);

        var limitedOutput = BacklogTextView.RenderBacklogTriage(items, goals, 1, 30, FixtureNow);

        Assert.Equal(new[]
        {
            "Backlog triage: open=8 done=1 active-linked=1 limit=1",
            "Stale open (>30d):",
            "- stale-01 age=45d Very stale open",
            "Active-linked open:",
            $"- linked-a age=1d goal={activeGoalId.Value[..8]}:Active Linked active work",
            "Blocked-looking open:",
            "- blocked1 age=3d Blocked on provider quota",
            "High-priority open:",
            "- urgent-1 age=4d P1 urgent gate fix",
            "Duplicate-looking open:",
            "- 2x fix the build: dup-0001 Fix the build! | dup-0002 fix-the build"
        }, limitedOutput.Split(Environment.NewLine).SkipLast(1).ToArray());
    }

    [Fact]
    public void ListLineAndMarkdownRenderers_KeepTheirText()
    {
        var created = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
        var updated = new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero);
        var plain = new BacklogItem("plain", "Plain", "", BacklogItemStatus.Open, created, updated, null);
        var superseded = plain with { Status = BacklogItemStatus.Superseded, SupersededBy = "replacement-item" };
        var duplicate = plain with
        {
            Id = "duplicate-item",
            Links = [new BacklogLink("canonical-item", "duplicate-item", BacklogLinkKind.Duplicate, "canonical-item", created)]
        };
        var related = plain with
        {
            Id = "related-one",
            Links = [new BacklogLink("related-one", "related-two", BacklogLinkKind.Related, null, created)]
        };

        Assert.Equal("[Superseded→replacem]", BacklogTextView.RenderBacklogListTag(superseded));
        Assert.Equal("[Dup→canonica]", BacklogTextView.RenderBacklogListTag(duplicate));
        Assert.Equal("[Open]", BacklogTextView.RenderBacklogListTag(plain));
        Assert.Equal(" [created=2026-07-01] [updated=2026-07-02]", BacklogTextView.RenderBacklogTimestampSuffix(plain));
        Assert.Equal(" [Related→related-]", BacklogTextView.RenderBacklogListSuffix(related));
        Assert.Equal("", BacklogTextView.RenderBacklogListSuffix(plain));

        var markdown = BacklogTextView.RenderBacklogMarkdown([
            plain with { Id = "open-a", Title = "Open A", Body = "Body A" },
            plain with { Id = "open-b", Title = "Open B" },
            plain with
            {
                Id = "done-c", Title = "Done C", Status = BacklogItemStatus.Done,
                UpdatedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)
            }
        ]);
        var lines = markdown.Split(Environment.NewLine);

        Assert.Contains("Body A", lines);
        Assert.Contains("- **Done C** — 2026-07-01", lines);
        Assert.Equal(4, lines.Count(line => line.StartsWith('#')));
        var openBIndex = Array.IndexOf(lines, "## Open B");
        Assert.True(openBIndex >= 0);
        Assert.Equal(new[] { "## Open B", "", "## Shipped (closed)" }, lines[openBIndex..(openBIndex + 3)]);
    }

    [Fact]
    public void BacklogRendering_HasOneOwner()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        string[] movedNames =
        [
            "RenderBacklogListTag", "RenderBacklogTimestampSuffix", "RenderBacklogListSuffix",
            "RenderBacklogTriage", "RenderBacklogMarkdown", "AppendTriageBucket",
            "AppendDuplicateBucket", "IsBlockedLooking", "IsHighPriorityLooking", "ContainsAny", "DuplicateKey"
        ];
        string[] removedNames = ["ShortBacklogId", "IsTerminalGoalStatus"];
        var handlerMethods = typeof(CliCommandHandlers).GetMethods(flags);
        var viewMethods = typeof(BacklogTextView).GetMethods(flags);

        Assert.True(typeof(BacklogTextView).IsAbstract);
        Assert.True(typeof(BacklogTextView).IsSealed);
        foreach (var name in movedNames)
        {
            Assert.DoesNotContain(handlerMethods, method => method.Name == name);
            Assert.True(Assert.Single(viewMethods, method => method.Name == name).IsStatic);
        }
        foreach (var name in removedNames)
        {
            Assert.DoesNotContain(handlerMethods, method => method.Name == name);
            Assert.DoesNotContain(viewMethods, method => method.Name == name);
        }
        Assert.Single(handlerMethods, method => method.Name == "ApplyBacklogListFilters");
    }

    private static (BacklogItem[] Items, IReadOnlyCollection<Goal> Goals, GoalId ActiveGoalId) CreateTriageFixture()
    {
        BacklogItem Item(string id, string title, BacklogItemStatus status, int daysBeforeNow) =>
            new(id, title, "", status, FixtureNow.AddDays(-daysBeforeNow), FixtureNow.AddDays(-daysBeforeNow), null);

        BacklogItem[] items =
        [
            Item("stale-01", "Very stale open", BacklogItemStatus.Open, 45),
            Item("fresh-01", "Fresh open", BacklogItemStatus.Open, 2),
            Item("closed-1", "Closed stale", BacklogItemStatus.Done, 60),
            Item("blocked1", "Blocked on provider quota", BacklogItemStatus.Open, 3),
            Item("urgent-1", "P1 urgent gate fix", BacklogItemStatus.Open, 4),
            Item("dup-0001", "Fix the build!", BacklogItemStatus.Open, 5),
            Item("dup-0002", "fix-the build", BacklogItemStatus.Open, 6),
            Item("linked-a", "Linked active work", BacklogItemStatus.Open, 1),
            Item("linked-t", "Linked terminal work", BacklogItemStatus.Open, 40)
        ];
        var clock = new FixedClock(FixtureNow);
        var kernel = new AgentOrchestratorKernel(clock);
        var activeGoal = kernel.CreateGoal("Active linked objective");
        var terminalGoal = kernel.CreateGoal("Terminal linked objective");
        kernel.ActivateGoal(activeGoal.Id, AgentCatalog.Default().Agents);
        kernel.SetGoalSourceBacklogItemId(activeGoal.Id, "linked-a");
        kernel.SetGoalSourceBacklogItemId(terminalGoal.Id, "linked-t");
        kernel = WithGoalStatus(kernel, terminalGoal.Id, GoalStatus.Completed, clock);
        return (items, kernel.Goals, activeGoal.Id);
    }

    private static AgentOrchestratorKernel WithGoalStatus(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        GoalStatus status,
        IClock clock)
    {
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals
                .Select(goal => goal.Id == goalId.Value ? goal with { Status = status } : goal)
                .ToArray()
        }, clock);
    }

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;
}

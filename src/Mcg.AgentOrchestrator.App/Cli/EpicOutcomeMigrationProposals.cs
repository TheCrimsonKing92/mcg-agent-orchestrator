using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

// One proposal per actual epic. Descriptions are context, never inferred policy or owner approval.
internal static class EpicOutcomeMigrationProposals
{
    internal static bool IsCommand(IReadOnlyList<string> parts) => parts.Count > 0 &&
        parts[0].Equals("epic-outcome-proposals", StringComparison.OrdinalIgnoreCase) && !CliCommandHelp.IsCommandSpecificHelp(parts);

    internal static bool Execute(IReadOnlyList<string> parts, OrchestratorWorkspace workspace)
    {
        CliCommandHelp.ThrowIfInvalidFlags(parts);
        var json = EpicProgressCommands.RequireJsonOnce(parts);
        if (parts.Count != (json ? 2 : 1) || parts.Skip(1).Any(part => part != "--json"))
            throw new ArgumentException("Usage: epic-outcome-proposals [--json]");
        var proposals = EpicProgressReadModel.Load(workspace).Select(row => new
        {
            epicId = row.Epic.Id, title = row.Epic.Title, description = row.Epic.Description,
            currentVersion = row.Outcome.BarVersion, legacyBar = row.Plan?.Plan.Bar,
            outcome = row.Outcome, action = "retain unknown pending owner decision",
            ownerDecisionsRequired = new[]
            {
                "Approve measurable requirements with stable ids; do not infer them from the description.",
                "Name each requirement's authoritative evidence issuer and file-sha256 receipt contract.",
                "Approve the definition at the displayed expected version; separately observe met/unmet evidence."
            },
            defineCommand = $"epic-outcome-define {row.Epic.Id} --text-file <approved-definition.json> --operator-actor <owner> --idempotency-key <unique-key>",
            recordCommand = $"epic-outcome-record {row.Epic.Id} --text-file <observed-evidence.json> --operator-actor <owner> --idempotency-key <unique-key>",
            decisionCommand = $"epic-decide {row.Epic.Id} --text-file <owner-decision.txt> --by <owner>"
        }).ToArray();
        if (json) Console.WriteLine(AgentReadEnvelope.Serialize("mcg.epic-outcome-proposals", 1,
            new { proposals }, EpicProgressAgentReadView.Options));
        else
        {
            if (proposals.Length == 0) Console.WriteLine("No epics; no migration proposals.");
            foreach (var proposal in proposals)
            {
                Console.WriteLine($"Epic: {proposal.title} ({proposal.epicId})");
                Console.WriteLine($"Current version: {proposal.currentVersion}; legacy Bar: {proposal.legacyBar ?? "(no bar)"}");
                Console.WriteLine(proposal.outcome.Render());
                Console.WriteLine($"Proposal: {proposal.action}");
                foreach (var decision in proposal.ownerDecisionsRequired) Console.WriteLine($"  Owner decision: {decision}");
                Console.WriteLine($"  {proposal.decisionCommand}");
                Console.WriteLine($"  {proposal.defineCommand}");
                Console.WriteLine($"  {proposal.recordCommand}");
            }
        }
        return false;
    }
}

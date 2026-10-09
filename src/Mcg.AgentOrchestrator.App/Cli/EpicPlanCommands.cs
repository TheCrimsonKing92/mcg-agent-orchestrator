using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class EpicPlanCommands
{
    internal static bool Execute(string command, IReadOnlyList<string> parts, CliExecutionContext context)
    {
        var (positions, flags) = Parse(parts);
        var expected = command == "epic-plan-move" ? 3 : command is "epic-plan-remove" or "epic-plan-done" ? 2 : 1;
        if (positions.Count != expected || string.IsNullOrWhiteSpace(positions[0]))
            throw new ArgumentException($"Invalid arguments for {command}; use {command} --help.");
        int? at = flags.TryGetValue("--at", out var atValue) ? Number(atValue) : null;
        var from = expected > 1 ? Number(positions[1]) : 0;
        var to = expected > 2 ? Number(positions[2]) : 0;
        string? text = null;
        if (command == "epic-plan-add")
        {
            if (flags.ContainsKey("--backlog") == flags.ContainsKey("--step-file"))
                throw new ArgumentException("Supply exactly one of --backlog or --step-file.");
            if (flags.TryGetValue("--step-file", out var path)) text = ReadText(path, allowBlank: false);
        }
        if (command is "epic-decide" or "epic-bar")
        {
            if (!flags.TryGetValue("--text-file", out var path)) throw new ArgumentException("--text-file is required.");
            text = ReadText(path, allowBlank: command == "epic-bar");
        }

        if (!File.Exists(context.Workspace.PortfolioStorePath))
            throw new InvalidOperationException($"Epic '{positions[0]}' was not found.");
        if (command == "epic-plan")
        {
            var portfolio = PortfolioStore.OpenReadOnly(context.Workspace.PortfolioStorePath);
            var readEpic = portfolio.ResolveEpicAsync(positions[0]).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"Epic '{positions[0]}' was not found.");
            PrintPlan(readEpic, EpicPlanStatusReader.Load(context.Workspace, [readEpic], includeReasons: true)[readEpic.Id]);
            return false;
        }
        var (epic, legacy) = EpicPlanWritePreparation.Resolve(context.Workspace.PortfolioStorePath, positions[0]);

        // All file/prefix parsing precedes a writable open. The store revalidates row state under its transaction.
        string? backlogId = null;
        if (flags.TryGetValue("--backlog", out var reference))
        {
            var matches = BoardFillBacklogSnapshot.Read(context.Workspace.BacklogStorePath)
                .Where(item => item.Id.StartsWith(reference, StringComparison.OrdinalIgnoreCase)).ToArray();
            backlogId = matches.Length switch
            {
                0 => throw new InvalidOperationException($"No backlog item found with id prefix '{reference}'."),
                1 => matches[0].Id,
                _ => throw new InvalidOperationException($"Backlog item prefix '{reference}' is ambiguous.")
            };
        }
        if (legacy) EpicPlanWritePreparation.ValidateLegacy(context.Workspace.PortfolioStorePath, epic, command, backlogId, at);
        var store = new EpicPlanStore(context.Workspace.PortfolioStorePath);
        var write = command switch
        {
            "epic-plan-add" when backlogId is not null => store.AddSliceAsync(epic.Id, backlogId, at),
            "epic-plan-add" => store.AddStepAsync(epic.Id, text!, at),
            "epic-plan-move" => store.MoveAsync(epic.Id, from, to),
            "epic-plan-remove" => store.RemoveAsync(epic.Id, from),
            "epic-plan-done" => store.MarkStepDoneAsync(epic.Id, from),
            "epic-decide" => store.AddDecisionAsync(epic.Id, text!, flags.GetValueOrDefault("--by")),
            "epic-bar" => store.SetBarAsync(epic.Id, text),
            _ => throw new InvalidOperationException($"Unknown plan command '{command}'.")
        };
        write.GetAwaiter().GetResult();
        Console.WriteLine($"Updated epic plan: [{epic.Id}] {epic.Title}");
        return false;
    }

    private static void PrintPlan(PortfolioEpic epic, EpicPlanView view)
    {
        Console.WriteLine($"Epic: {epic.Title} ({epic.Id})");
        Console.WriteLine($"Purpose: {epic.Description ?? "(no purpose)"}");
        Console.WriteLine($"Bar: {view.Plan.Bar ?? "(no bar)"}");
        Console.WriteLine("Plan:");
        foreach (var item in view.Items)
            Console.WriteLine($"{item.Item.Position}. {item.Subject} — {item.Render()}");
        Console.WriteLine("Decisions (newest first):");
        foreach (var decision in view.Plan.Decisions)
            Console.WriteLine($"  {decision.DecidedAt.UtcDateTime:O}" + (decision.DecidedBy is null ? "" : $" by {decision.DecidedBy}") + $": {decision.Text}");
        var next = EpicPlanNextStep.Compute(view.Items);
        Console.WriteLine(next.IsStalled ? next.Text : $"Next step: {next.Text}");
        Console.WriteLine("Not in plan:");
        foreach (var member in view.UnplannedMembers)
            Console.WriteLine($"  {member.Kind}: {member.MemberId}");
    }

    private static (List<string> Positions, Dictionary<string, string> Flags) Parse(IReadOnlyList<string> parts)
    {
        var positions = new List<string>();
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < parts.Count; index++)
        {
            var part = parts[index];
            if (!part.StartsWith("--", StringComparison.Ordinal)) { positions.Add(part); continue; }
            var equals = part.IndexOf('=');
            var name = equals < 0 ? part : part[..equals];
            var value = equals < 0
                ? ++index < parts.Count && !parts[index].StartsWith("--", StringComparison.Ordinal) ? parts[index] : throw new ArgumentException($"{name} requires a value.")
                : part[(equals + 1)..];
            if (string.IsNullOrWhiteSpace(value) || !flags.TryAdd(name, value))
                throw new ArgumentException($"{name} requires one nonblank value.");
        }
        return (positions, flags);
    }

    private static int Number(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
        ? number : throw new ArgumentException("Plan positions must be positive integers.");

    private static string ReadText(string path, bool allowBlank)
    {
        var text = File.ReadAllText(path).Trim();
        if (!allowBlank && string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text file must not be blank.");
        return text;
    }
}

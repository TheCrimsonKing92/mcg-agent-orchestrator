using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Derives checks from snapshot facts without inspecting the repository again.</summary>
public static class LearnedTestCheckDeriver
{
    public static (IReadOnlyList<LearnedTestCheck> Checks, IReadOnlyList<UnresolvedTestUnit> Unresolved) Derive(ProjectModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var checks = new List<LearnedTestCheck>();
        var unresolved = new List<UnresolvedTestUnit>();
        foreach (var unit in model.Units.Where(unit => unit.IsTest.Value == true))
        {
            var runner = model.TestSetups.FirstOrDefault(setup => setup.UnitId == unit.Id)?.Runner;
            if (runner is { Value: "VSTest" or "MTP", Confidence: FactConfidence.High or FactConfidence.Medium })
            {
                checks.Add(new LearnedTestCheck(unit.Id.Replace('\\', '/'), runner.Value.ToLowerInvariant(),
                    runner.Confidence, runner.Source));
            }
            else
            {
                var key = $"commands/{unit.Id}/test";
                var question = model.OwnerQuestions.FirstOrDefault(question => question.FactKey == key);
                unresolved.Add(new UnresolvedTestUnit(unit.Id, question?.FactKey ?? key));
            }
        }

        return (checks, unresolved);
    }
}

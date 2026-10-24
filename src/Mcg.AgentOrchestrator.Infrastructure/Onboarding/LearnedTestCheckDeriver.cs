using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Derives checks from snapshot facts without inspecting the repository again.</summary>
public static class LearnedTestCheckDeriver
{
    public static (IReadOnlyList<LearnedTestCheck> Checks, IReadOnlyList<UnresolvedTestUnit> Unresolved) Derive(ProjectModel model) =>
        Derive(model, new AppliedOwnerAnswers([], [], []));

    public static (IReadOnlyList<LearnedTestCheck> Checks, IReadOnlyList<UnresolvedTestUnit> Unresolved) Derive(
        ProjectModel model, AppliedOwnerAnswers applied)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(applied);
        var checks = new List<LearnedTestCheck>();
        var unresolved = new List<UnresolvedTestUnit>();
        foreach (var unit in model.Units.Where(unit => unit.IsTest.Value == true))
        {
            var runner = model.TestSetups.FirstOrDefault(setup => setup.UnitId == unit.Id)?.Runner;
            var key = $"commands/{unit.Id}/test";
            // Owner answers settle only unresolved runners; confident discovery retains its provenance.
            if (!Confident(runner) &&
                applied.AnsweredFacts.LastOrDefault(fact => fact.FactKey == key) is { } answered)
                runner = new ProjectFact<string>(OwnerAnswerValidator.Validate(key, answered.Fact.Value),
                    answered.Fact.Source, FactConfidence.High);
            if (runner is not null && Confident(runner))
            {
                checks.Add(new LearnedTestCheck(unit.Id.Replace('\\', '/'), runner.Value.ToLowerInvariant(),
                    runner.Confidence, runner.Source,
                    model.Stack == ProjectStack.Go ? LearnedTestCheckKind.GoTest : LearnedTestCheckKind.DotnetTest));
            }
            else
            {
                var question = model.OwnerQuestions.FirstOrDefault(question => question.FactKey == key);
                unresolved.Add(new UnresolvedTestUnit(unit.Id, question?.FactKey ?? key));
            }
        }

        return (checks, unresolved);

        bool Confident(ProjectFact<string>? runner) =>
            runner is { Confidence: FactConfidence.High or FactConfidence.Medium } &&
            (model.Stack == ProjectStack.Go ? runner.Value == "go" : runner.Value is "VSTest" or "MTP");
    }
}

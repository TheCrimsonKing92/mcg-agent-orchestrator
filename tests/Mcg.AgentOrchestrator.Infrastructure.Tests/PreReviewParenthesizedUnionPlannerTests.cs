using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: the real planner reads only this test's unique temporary repository fixture.
public sealed class PreReviewParenthesizedUnionPlannerTests
{
    [Fact]
    public void ContinuitySupervisorShapedChange_BuildsSplitFocusedRequest()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".git"), string.Empty);
            var classNames = new[]
                {
                    "ConductorContinuitySupervisorTests",
                    "ConductorRunClosureCheckTests"
                }
                .Concat(Enumerable.Range(1, 25).Select(index => $"ContinuityRunClosureCompanion{index:D2}Tests"))
                .ToArray();
            var testPaths = classNames.Select(name =>
                $"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/{name}.cs").ToArray();
            Assert.Equal(27, testPaths.Length);
            for (var index = 0; index < testPaths.Length; index++)
            {
                var absolutePath = Path.Combine(root, testPaths[index]);
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
                File.WriteAllText(absolutePath,
                    $"public sealed class {classNames[index]} {{ [Xunit.Fact] public void Runs() {{ }} }}");
            }
            var changedPaths = new[]
                {
                    "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorContinuitySupervisor.cs",
                    "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorRunClosureCheck.cs"
                }
                .Concat(testPaths).ToArray();
            var summary = RepositoryChangeClassifier.Classify(changedPaths);

            var plan = RepositoryTestImpactPlanner.Plan(summary, root);

            var check = Assert.Single(plan.Checks.Where(check => check.Command.Contains("--filter")));
            Assert.Equal(RepositoryTestProject.Infrastructure, check.TestProject);
            var filterIndex = Array.IndexOf(check.Command.ToArray(), "--filter");
            var filter = check.Command[filterIndex + 1];
            Assert.StartsWith("(", filter);
            Assert.Contains(")|(", filter);
            Assert.True(1 + filter.Length > 1024);
            Assert.NotNull(check.TestClassSelections);
            Assert.Equal(27, check.TestClassSelections.Count);
            Assert.Equal(classNames.Order(StringComparer.Ordinal),
                check.TestClassSelections.Order(StringComparer.Ordinal));

            var context = PreReviewEvidenceContextBuilder.Build("parenthesized-union-candidate", summary, plan);

            Assert.False(context.MappingNeedsInput, context.MappingReason);
            Assert.False(context.NoApplicableTests);
            Assert.False(string.IsNullOrWhiteSpace(context.FocusedRequest));
            var items = context.SelectedFocusedTests;
            Assert.True(items.Count > 1);
            Assert.Equal(string.Join("; ", items), context.FocusedRequest);
            Assert.All(items, item =>
            {
                Assert.StartsWith("Infrastructure.Tests:", item);
                Assert.InRange(item[(item.IndexOf(':') + 1)..].Length, 1, 1024);
            });
            var emittedClauses = items.SelectMany(item =>
                item[(item.IndexOf(':') + 1)..].Trim().Split('|')).ToArray();
            Assert.Equal(check.TestClassSelections.Select(name => $"FullyQualifiedName~{name}"), emittedClauses);
            Assert.Equal(27, emittedClauses.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

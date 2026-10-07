using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each resolution owns a fresh temporary filesystem root.
public sealed class PlannerOutputContractTestsDocsTargetCitation : WorkerDispatchTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("`docs/operator-runbook.md`")]
    [Xunit.InlineData("`docs/operator-runbook.md:12`")]
    [Xunit.InlineData("`docs/operator-runbook.md:12-20`")]
    [Xunit.InlineData("`scripts/Run-Probe.ps1`")]
    [Xunit.InlineData("`config/acceptance-manifest.json`")]
    [Xunit.InlineData("` docs\\operator-runbook.md:12-20 `")]
    public void RepositoryPathsSatisfyTargetSection(string targetBody)
    {
        var result = ResolveWithTargetBody(targetBody);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    [Xunit.Theory]
    [Xunit.InlineData("`notes/operator-runbook.md`")]
    [Xunit.InlineData("`two-word`")]
    [Xunit.InlineData("docs/operator-runbook.md:12 beside `two-word`")]
    [Xunit.InlineData("`docs/`")]
    [Xunit.InlineData("`scripts/`")]
    [Xunit.InlineData("`config/`")]
    [Xunit.InlineData("`Docs/operator-runbook.md`")]
    public void NonTargetCitationsFailSectionEvidence(string targetBody)
    {
        var result = ResolveWithTargetBody(targetBody);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("must cite a concrete target seam or symbol", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MissingRepositoryPathStillFailsExistenceCheck()
    {
        var result = ResolveWithTargetBody("`docs/absent-page.md:4`");

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("docs/absent-page.md", result.Diagnostic, StringComparison.Ordinal);
        Xunit.Assert.Contains("does not exist and is not marked as a new file", result.Diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void NewFileMarkerPermitsMissingRepositoryPath()
    {
        var result = ResolveWithTargetBody("`docs/absent-page.md:4` (new file)");

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
    }

    private static PlannerOutputContractResult ResolveWithTargetBody(string targetBody)
    {
        var workingDirectory = CreateTempDirectory();
        try
        {
            foreach (var relativePath in new[]
                     {
                         "docs/operator-runbook.md", "scripts/Run-Probe.ps1", "config/acceptance-manifest.json"
                     })
            {
                var path = Path.Combine(workingDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Empty);
            }

            var plan = PlannerContractPlanFixture().ReplaceLineEndings("\n");
            const string heading = "## Target seams and symbols";
            var bodyStart = plan.IndexOf(heading, StringComparison.Ordinal) + heading.Length;
            var bodyEnd = plan.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
            const string lead = "The planned refresh changes exactly one file, cited here: ";
            plan = plan[..bodyStart] + "\n\n" + lead + targetBody + "\n" + plan[bodyEnd..];

            return PlannerOutputContract.Resolve(plan, string.Empty, workingDirectory);
        }
        finally
        {
            // Cleanup is best-effort and cannot change the contract verdict.
            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

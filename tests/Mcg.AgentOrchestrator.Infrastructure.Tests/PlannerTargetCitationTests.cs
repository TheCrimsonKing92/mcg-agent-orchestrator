using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: contract resolutions own isolated temporary filesystem roots.
public sealed class PlannerTargetCitationTests : WorkerDispatchTestSupport
{
    [Xunit.Theory]
    [Xunit.InlineData("`internal/color/color.go::shouldEnableColor`")]
    [Xunit.InlineData("`internal/color/color.go:57`")]
    [Xunit.InlineData("`cmd/schemadrift/main.go:10-20`")]
    [Xunit.InlineData("`pkg/x/y.py`")]
    [Xunit.InlineData("`web/src/app.ts::render`")]
    [Xunit.InlineData("`src/Foo.cs`")]
    [Xunit.InlineData("`shouldEnableColor`")]
    [Xunit.InlineData("`internal\\color\\color.go::shouldEnableColor`")]
    [Xunit.InlineData("`./internal/../pkg/x/y.py`")]
    [Xunit.InlineData("`web/my app/component.ts`")]
    [Xunit.InlineData("`tools/probe.ps1`")]
    [Xunit.InlineData("`data/settings.json`")]
    [Xunit.InlineData("`deploy/workflow.yaml`")]
    [Xunit.InlineData("`internal/color/color.go#L57-L60`")]
    public void BacktickedTargetsAreRecognized(string body)
    {
        Xunit.Assert.True(PlannerTargetCitation.HasTargetCitation(body));
    }

    [Xunit.Theory]
    [Xunit.InlineData("internal/color/color.go::shouldEnableColor")]
    [Xunit.InlineData("src/Foo.cs beside `the main loop`")]
    [Xunit.InlineData("`the main loop`")]
    [Xunit.InlineData("`internal/color`")]
    [Xunit.InlineData("`internal.v2/color`")]
    [Xunit.InlineData("`internal/.go`")]
    [Xunit.InlineData("`internal/color.`")]
    [Xunit.InlineData("`internal//color.go`")]
    [Xunit.InlineData("`/internal/color.go`")]
    [Xunit.InlineData("`C:\\internal\\color.go`")]
    [Xunit.InlineData("`\\\\server\\share\\color.go`")]
    [Xunit.InlineData("`../internal/color.go`")]
    [Xunit.InlineData("`internal/../../color.go`")]
    [Xunit.InlineData("`internal/color.go::`")]
    [Xunit.InlineData("`internal/color.go::some method`")]
    [Xunit.InlineData("`internal/color.go:line`")]
    [Xunit.InlineData("`internal/color.go:57x`")]
    [Xunit.InlineData("`internal/color.go:10-`")]
    [Xunit.InlineData("`internal/color.go:10-20:30`")]
    [Xunit.InlineData("`a`")]
    [Xunit.InlineData("` shouldEnableColor `")]
    public void ProseDirectoriesAndMalformedPathsAreRejected(string body)
    {
        Xunit.Assert.False(PlannerTargetCitation.HasTargetCitation(body));
    }

    [Xunit.Theory]
    [Xunit.InlineData("`src/NoExtension`")]
    [Xunit.InlineData("`TESTS\\NoExtension`")]
    [Xunit.InlineData("`*.cs`")]
    [Xunit.InlineData("`Program.cs:12`")]
    [Xunit.InlineData("`Program.cs::Some Method`")]
    public void NamedLegacyFormsRemainAccepted(string body)
    {
        Xunit.Assert.True(PlannerTargetCitation.HasTargetCitation(body));
    }

    [Xunit.Fact]
    public void DocumentationBoundaryDoesNotRestrictCodeLayouts()
    {
        Xunit.Assert.False(PlannerTargetCitation.HasTargetCitation("`notes/x.md`"));
        Xunit.Assert.True(PlannerTargetCitation.HasTargetCitation("`internal/x/y.go`"));
    }

    [Xunit.Theory]
    [Xunit.InlineData("docs/x.md", true)]
    [Xunit.InlineData("scripts/x.markdown:12", true)]
    [Xunit.InlineData("config/x.txt", true)]
    [Xunit.InlineData("docs\\x.rst::Section", true)]
    [Xunit.InlineData("notes/x.md", false)]
    [Xunit.InlineData("notes/x.markdown", false)]
    [Xunit.InlineData("notes/x.txt", false)]
    [Xunit.InlineData("notes/x.rst", false)]
    [Xunit.InlineData("Docs/x.md", false)]
    public void DocumentationUsesLegacyCaseSensitivePrefixes(string citation, bool expected)
    {
        Xunit.Assert.Equal(expected, PlannerTargetCitation.HasTargetCitation($"`{citation}`"));
    }

    [Xunit.Theory]
    [Xunit.InlineData("internal/color/color.go::shouldEnableColor", "internal/color/color.go")]
    [Xunit.InlineData("internal/color/color.go::Color.cs", "internal/color/color.go")]
    [Xunit.InlineData("internal/color/color.go:57", "internal/color/color.go")]
    [Xunit.InlineData("cmd/schemadrift/main.go:10-20", "cmd/schemadrift/main.go")]
    [Xunit.InlineData("web/src/app.ts::render", "web/src/app.ts")]
    [Xunit.InlineData("pkg/x/y.py", "pkg/x/y.py")]
    [Xunit.InlineData(" internal\\color\\color.go:57 ", "internal\\color\\color.go")]
    [Xunit.InlineData("./internal/../pkg/x/y.py:10", "./internal/../pkg/x/y.py")]
    [Xunit.InlineData("internal/color/color.go#L57-L60", "internal/color/color.go")]
    public void RepositoryPathExtractionRemovesOnlyCitationSuffix(string citation, string expected)
    {
        Xunit.Assert.True(PlannerTargetCitation.TryGetRepositoryPath(citation, out var path));
        Xunit.Assert.Equal(expected, path);
    }

    [Xunit.Fact]
    public void LiveGoSectionIsAcceptedAndRemovingItsBackticksProducesExistingDiagnostic()
    {
        WithGoRepository(root =>
        {
            const string targetBody = "- `internal/color/color.go::shouldEnableColor`: in the default branch at `internal/color/color.go:57`, preserve the existing color detection behavior.";
            var accepted = PlannerOutputContract.Resolve(PlanWithTargetBody(targetBody), string.Empty, root);
            Xunit.Assert.True(accepted.Succeeded, accepted.Diagnostic);
            Xunit.Assert.Contains(targetBody, accepted.Plan, StringComparison.Ordinal);

            var rejected = PlannerOutputContract.Resolve(
                PlanWithTargetBody(targetBody.Replace("`", string.Empty, StringComparison.Ordinal)),
                string.Empty,
                root);

            Xunit.Assert.False(rejected.Succeeded);
            Xunit.Assert.Contains(
                "must cite a concrete target seam or symbol in backticks in its section body",
                rejected.Diagnostic,
                StringComparison.Ordinal);
        });
    }

    [Xunit.Theory]
    [Xunit.InlineData("::shouldEnableColor")]
    [Xunit.InlineData("::Color.cs")]
    [Xunit.InlineData(":57")]
    [Xunit.InlineData(":10-20")]
    public void GoSuffixIsRemovedBeforeExistingFileCasingCheck(string suffix)
    {
        WithGoRepository(root =>
        {
            var citation = "internal/color/Color.go" + suffix;
            var result = PlannerOutputContract.Resolve(PlanWithTargetBody($"- Inspect `{citation}`."), string.Empty, root);

            Xunit.Assert.False(result.Succeeded);
            Xunit.Assert.Contains($"target citation '{citation}' does not exist and is not marked as a new file", result.Diagnostic, StringComparison.Ordinal);
            Xunit.Assert.Contains("Did you mean `internal/color/color.go`?", result.Diagnostic, StringComparison.Ordinal);
        });
    }

    [Xunit.Fact]
    public void NewGoFileMarkerKeepsItsExistingMeaningAfterSuffixNormalization()
    {
        WithGoRepository(root =>
        {
            // This casing mismatch rejects without the marker in the preceding theory.
            var result = PlannerOutputContract.Resolve(
                PlanWithTargetBody("- `internal/color/Color.go::shouldEnableColor` (new file)"), string.Empty, root);

            Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        });
    }

    private static string PlanWithTargetBody(string targetBody)
    {
        var plan = PlannerContractPlanFixture().ReplaceLineEndings("\n");
        const string heading = "## Target seams and symbols";
        var bodyStart = plan.IndexOf(heading, StringComparison.Ordinal) + heading.Length;
        var bodyEnd = plan.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        return plan[..bodyStart] + "\n\n" + targetBody + "\n" + plan[bodyEnd..];
    }

    private static void WithGoRepository(Action<string> assertion)
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "internal", "color", "color.go");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "package color\nfunc shouldEnableColor() bool { return true }\n");
            Xunit.Assert.True(File.Exists(path), "The Go repository fixture must exist before contract validation.");
            assertion(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

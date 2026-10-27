namespace Mcg.AgentOrchestrator.Core;

internal static class RoleRequirementStackGuidance
{
    private const string DeveloperDeferred = "- Name the failing test in `tests: deferred`; never run tests.";
    private const string DeveloperBuild = "- Build changed projects with the worker build check.";
    private const string TesterEvidence = "- Name the test classes to run in evidence_request; never run tests.";
    private const string TesterBuild = "- Build only with the worker build check; do not modify source files.";
    private const string TesterSelection = "- Conductor-run selections use FullyQualifiedName~Class or Name~Method-symbol syntax and reject DisplayName text and a bare class name.";

    private const string GoDeveloperReport = "- Report each go command and its result in `tests`.";
    private const string GoDeveloperBuild = "- Build and test with the go commands in the Standing Rules.";
    private const string GoTesterEvidence = "- Name the packages and tests to run in evidence_request.";
    private const string GoTesterBuild = "- Build only with `go build ./...`; do not modify source files.";
    private const string GoTesterSelection = "- Go selections: test_project is a package directory relative to go.mod; test_class is test names joined by `|`, such as `TestParse|TestFormat`.";

    private const string NeutralDeveloperReport = "- Report each command and its result in `tests`.";
    private const string NeutralDeveloperBuild = "- Build and test with the commands in the Standing Rules.";
    private const string NeutralTesterEvidence = "- Name the tests to run in evidence_request.";
    private const string NeutralTesterBuild = "- Build only with the project's own build command; do not modify source files.";

    public static IReadOnlyList<string> Apply(IReadOnlyList<string> lines, ProjectStack? stack)
    {
        if (stack == ProjectStack.Dotnet)
            return lines;

        var result = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            // String constant patterns use ordinal whole-line equality.
            var replacement = line switch
            {
                DeveloperDeferred => stack == ProjectStack.Go ? GoDeveloperReport : NeutralDeveloperReport,
                DeveloperBuild => stack == ProjectStack.Go ? GoDeveloperBuild : NeutralDeveloperBuild,
                TesterEvidence => stack == ProjectStack.Go ? GoTesterEvidence : NeutralTesterEvidence,
                TesterBuild => stack == ProjectStack.Go ? GoTesterBuild : NeutralTesterBuild,
                TesterSelection => stack == ProjectStack.Go ? GoTesterSelection : null,
                _ => line
            };
            if (replacement is not null)
                result.Add(replacement);
        }
        return result;
    }
}

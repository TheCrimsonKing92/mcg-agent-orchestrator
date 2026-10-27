using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// The standing criterion, worker guidance and recognition rule for one target stack.
internal sealed class AuthorBriefDraftStackCriteria
{
    private readonly Toolchain? _stack;

    private AuthorBriefDraftStackCriteria(Toolchain? stack) => _stack = stack;

    internal static AuthorBriefDraftStackCriteria For(Toolchain? stack) => new(stack);

    private bool UsesHouseConvention => _stack is null or Toolchain.Dotnet;

    internal string Introduction => UsesHouseConvention
        ? "The last two acceptance criteria are the following house Developer deferred-tests criterion, copied character for character, followed by one single-sentence Reviewer criterion:"
        : "The last two acceptance criteria are the following standing Developer acceptance-manifest criterion, copied character for character, followed by one single-sentence Reviewer criterion:";

    internal string Criterion => _stack switch
    {
        null or Toolchain.Dotnet =>
            "The Developer reports `tests: deferred - ` followed, directly after the hyphen and comma-separated, by every test class it touched or added. The Tester's evidence_request runs them. Developer owns; Acceptance executes. TEST-VERIFIABLE.",
        Toolchain.Go =>
            "The acceptance manifest checks pass: go build -buildvcs=false ./..., go vet ./... and go test -count=1 ./.... Developer owns; Acceptance executes. TEST-VERIFIABLE.",
        _ => $"The acceptance manifest checks pass using {GoalIntakeTargetStack.NeutralBuildTestTool}. Developer owns; Acceptance executes. TEST-VERIFIABLE."
    };

    internal string? WorkerNote => _stack switch
    {
        null or Toolchain.Dotnet => null,
        Toolchain.Go => "Workers run go build, go vet and go test themselves and never report test classes.",
        _ => "Workers follow the target repository’s build and test instructions and report evidence using that stack’s conventions."
    };

    // ParseDeclared already removes each criterion's numbering prefix.
    internal bool Matches(string criterion) => UsesHouseConvention
        ? criterion.Contains("tests: deferred - ", StringComparison.Ordinal) &&
            criterion.TrimEnd().EndsWith("Developer owns; Acceptance executes. TEST-VERIFIABLE.", StringComparison.Ordinal)
        : criterion.Trim().Equals(Criterion, StringComparison.OrdinalIgnoreCase);

    internal string PassDetail => UsesHouseConvention
        ? "Developer deferred-tests criterion present."
        : "Developer acceptance-manifest criterion present.";

    internal string FailureDetail(int count) => UsesHouseConvention
        ? $"Observed {count} declared criteria; none contains tests: deferred - and ends with Developer owns; Acceptance executes. TEST-VERIFIABLE."
        : $"Observed {count} declared criteria; expected standing criterion: {Criterion}";
}

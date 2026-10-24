namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>The ordered, repository-wide verification commands for a Go project.</summary>
public static class GoVerificationChecks
{
    public const string Command = "go";

    public sealed record Check(string Name, string Command, IReadOnlyList<string> Arguments);

    public static IReadOnlyList<Check> Entries { get; } = Array.AsReadOnly(new[]
    {
        new Check("go build", Command, Array.AsReadOnly(new[] { "build", "-buildvcs=false", "./..." })),
        new Check("go vet", Command, Array.AsReadOnly(new[] { "vet", "./..." })),
        new Check("go test", Command, Array.AsReadOnly(new[] { "test", "-count=1", "./..." }))
    });
}

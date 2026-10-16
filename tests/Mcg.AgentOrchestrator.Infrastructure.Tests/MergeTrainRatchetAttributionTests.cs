using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its source root and TRX artifacts.
public sealed class MergeTrainRatchetAttributionTests
{
    private const string FileViolation = "src/App/Big.cs has 7 lines, exceeding the recorded ceiling of 5. Extract behavior to a collaborator and lower the ceil";
    private const string ClassViolation = "class:Handlers has 12157, exceeding the class total-line ceiling of 12156. Moving members into another partial file";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryAttribute_RatchetSubject_ReturnsItsOnlyOwner(bool classSubject)
    {
        using var fixture = new Fixture();
        var members = fixture.Members;
        var receipt = fixture.Receipt(classSubject ? ClassViolation : FileViolation);
        Assert.Empty(receipt.GateTestResultPaths);
        var attributed = MergeTrainRedAttribution.TryAttribute(receipt, fixture.Root, members, out var subjects);
        Assert.Same(members[classSubject ? 1 : 0], attributed);
        Assert.Equal(new[] { classSubject ? "class:Handlers" : "src/App/Big.cs" }, subjects);
    }

    [Theory]
    [InlineData("two-owners")]
    [InlineData("no-owner")]
    [InlineData("different-owners")]
    [InlineData("check-only")]
    [InlineData("no-subject")]
    [InlineData("passed")]
    [InlineData("infrastructure")]
    public void TryAttribute_UnsafeOrAbsentEvidence_Declines(string fault)
    {
        using var fixture = new Fixture();
        var members = fixture.Members;
        var receipt = fixture.Receipt(FileViolation);
        switch (fault)
        {
            case "two-owners": members[2] = members[2] with { LandingPaths = ["src/App/Big.cs"] }; break;
            case "no-owner": members[0] = members[0] with { LandingPaths = ["other.cs"] }; break;
            case "different-owners": receipt = fixture.Receipt(FileViolation, ClassViolation); break;
            case "check-only": receipt = fixture.Receipt(); break;
            case "no-subject": receipt = fixture.Receipt(" has 7 lines, exceeding ceiling"); break;
            case "passed": receipt = receipt with { Outcome = MergeTrainGateOutcome.Passed }; break;
            case "infrastructure": receipt = receipt with { Outcome = MergeTrainGateOutcome.InfrastructureFailure }; break;
        }
        Assert.Null(MergeTrainRedAttribution.TryAttribute(receipt, fixture.Root, members, out var subjects));
        Assert.Empty(subjects);
    }

    [Fact]
    public void TryAttribute_OrdinaryTrxFailure_KeepsOwnershipPath()
    {
        using var fixture = new Fixture();
        var trx = Path.Combine(fixture.Root, "failed.trx");
        File.WriteAllText(trx, """
            <TestRun><Results><UnitTestResult testId="1" testName="Ns.Big.Fails" outcome="Failed" /></Results></TestRun>
            """);
        var receipt = fixture.Receipt() with { FailedChecks = ["gate"], GateTestResultPaths = [trx] };
        Assert.Same(fixture.Members[0], MergeTrainRedAttribution.TryAttribute(
            receipt, fixture.Root, fixture.Members, out var subjects));
        Assert.Equal(new[] { "Big" }, subjects);
    }

    [Fact]
    public void TryAttribute_NormalizedPathsAndMultipleSubjects_SortsSubjects()
    {
        using var fixture = new Fixture();
        fixture.Members[0] = fixture.Members[0] with
        {
            LandingPaths = [".\\SRC\\APP\\BIG.CS", Path.Combine(fixture.Root, "src/App/Other.cs")]
        };
        var receipt = fixture.Receipt("src/App/Other.cs has 9 lines", "ignored", FileViolation, FileViolation);
        Assert.Same(fixture.Members[0], MergeTrainRedAttribution.TryAttribute(
            receipt, fixture.Root, fixture.Members, out var subjects));
        Assert.Equal(new[] { "src/App/Big.cs", "src/App/Other.cs" }, subjects);
    }

    [Theory]
    [InlineData("class:Missing has 9 lines")]
    [InlineData("class:Bad.Name has 9 lines")]
    [InlineData("src/App/Big.txt has 9 lines")]
    public void TryAttribute_UnresolvedOrInvalidSubject_Declines(string violation)
    {
        using var fixture = new Fixture();
        Assert.Null(MergeTrainRedAttribution.TryAttribute(
            fixture.Receipt(violation), fixture.Root, fixture.Members, out _));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ratchet-attribution-" + Guid.NewGuid().ToString("N"));
        internal MergeTrainMemberBinding[] Members { get; } =
        [
            MergeTrainDropBacktestTests.Fixture.Member(1) with { LandingPaths = ["src/App/Big.cs"] },
            MergeTrainDropBacktestTests.Fixture.Member(2) with { LandingPaths = ["src/App/Handlers.Goals.cs"] },
            MergeTrainDropBacktestTests.Fixture.Member(3)
        ];

        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "src/App"));
            File.WriteAllText(Path.Combine(Root, "src/App/Big.cs"), "namespace Ns; public class Big { }");
            File.WriteAllText(Path.Combine(Root, "src/App/Handlers.cs"), "namespace Ns; public partial class Handlers { }");
            File.WriteAllText(Path.Combine(Root, "src/App/Handlers.Goals.cs"), "namespace Ns; public partial class Handlers { }");
        }

        internal MergeTrainReceipt Receipt(params string[] violations) => new("ratchet",
            MergeTrainIdentity.Create(Members, new string('a', 40), new string('b', 40), "manifest"),
            MergeTrainGateOutcome.Failed, DateTimeOffset.UnixEpoch, 0,
            new[] { SourceSizeRatchetPreflight.CheckName }.Concat(violations).ToArray(), 1, []);

        public void Dispose() => Directory.Delete(Root, true);
    }
}

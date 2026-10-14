using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class ConductorSupervisorReadinessIdentityTests
{
    [Xunit.Theory]
    [Xunit.InlineData("start-time")]
    [Xunit.InlineData("commit")]
    [Xunit.InlineData("token")]
    [Xunit.InlineData("directory")]
    public async Task MismatchedReadinessKeepsIncumbentAuthorityAndStopsOnlyLaunchedIdentity(string mismatch)
    {
        var fixture = new SupervisorHandoffFixture();
        fixture.Seam.TransformReady = ready => mismatch switch
        {
            "start-time" => ready with { Successor = ready.Successor with { StartedAt = ready.Successor.StartedAt.AddSeconds(1) } },
            "commit" => ready with { Build = ready.Build with { CommitSha = "wrong-commit" } },
            "token" => ready with { Token = "wrong-token" },
            "directory" => ready with { Build = ready.Build with { RunDirectory = "C:\\wrong-run" } },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };

        Assert.Equal(0, await fixture.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal(fixture.Seam.Self, fixture.Seam.Owner);
        Assert.Equal(0, fixture.Seam.Transfers);
        Assert.Equal(fixture.Seam.Successor, Assert.Single(fixture.Seam.Stopped));
        Assert.Equal(3, fixture.Host.Requests.Count);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task CancellationBeforeAuthorityTransferCleansUpPendingSuccessor(bool readyAlreadyPublished)
    {
        var fixture = new SupervisorHandoffFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        fixture.Seam.SuppressReadiness = !readyAlreadyPublished;
        fixture.Seam.BeforeReadReady = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync(cancellation.Token));
        Assert.Equal(fixture.Seam.Self, fixture.Seam.Owner);
        Assert.Equal(0, fixture.Seam.Transfers);
        Assert.Equal(fixture.Seam.Successor, Assert.Single(fixture.Seam.Stopped));
    }

    [Xunit.Fact]
    public async Task ValidReadinessTransfersAuthorityExactlyOnce()
    {
        var fixture = new SupervisorHandoffFixture();

        Assert.Equal(0, await fixture.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal(fixture.Seam.Successor, fixture.Seam.Owner);
        Assert.Equal(1, fixture.Seam.Transfers);
        Assert.Empty(fixture.Seam.Stopped);
    }

    [Xunit.Theory]
    [Xunit.InlineData("commit")]
    [Xunit.InlineData("token")]
    [Xunit.InlineData("owner")]
    public async Task InvalidInboundRecordCannotPublishReadinessOrStartAChild(string mismatch)
    {
        var fixture = new SupervisorHandoffFixture(inbound: true);
        var record = fixture.Seam.ReadRecord("inbound.json");
        if (mismatch == "commit")
            fixture.Seam.WriteRecord(record with { AdoptedBuild = record.AdoptedBuild with { CommitSha = "wrong-commit" } });
        else if (mismatch == "token")
            fixture.Seam.WriteRecord(record with { Token = "" });
        else
            fixture.Seam.Acquire(new ConductorSupervisorBuildIdentity("commit-a", "C:\\run-a"));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Seam.ReadinessPublications);
        Assert.Empty(fixture.Host.Requests);
    }
}

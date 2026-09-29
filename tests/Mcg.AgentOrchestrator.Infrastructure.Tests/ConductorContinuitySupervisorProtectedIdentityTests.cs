using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class ConductorContinuitySupervisorProtectedIdentityTests
{
    [Fact]
    public async Task SuccessorPassesItsOwnIdentityToConductorChild()
    {
        var fixture = new SupervisorHandoffFixture(inbound: true);
        ProtectedProcessIdentity? bound = null;
        fixture.BindProtectedIdentity = identity => bound = identity;

        Assert.Equal(0, await fixture.RunAsync());
        var expected = new ProtectedProcessIdentity(fixture.Seam.Self.ProcessId,
            fixture.Seam.Self.StartedAt.UtcDateTime.Ticks);
        Assert.Equal(expected, bound);
        Assert.NotEmpty(fixture.Host.Requests);
        foreach (var request in fixture.Host.Requests)
        {
            Assert.NotNull(request.AdditionalEnvironment);
            Assert.Equal(expected.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                request.AdditionalEnvironment[ProtectedProcessIdentity.PidVariable]);
            Assert.Equal(expected.StartTimeUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                request.AdditionalEnvironment[ProtectedProcessIdentity.StartTicksVariable]);
        }
    }
}

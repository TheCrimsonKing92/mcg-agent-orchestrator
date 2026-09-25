using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class CandidateIdentityTests
{
    [Fact]
    public void ValueEqualityRequiresAllThreeComponentsAndSurvivesSerialization()
    {
        var first = new CandidateIdentity("A1", "B2", "C3");
        var same = new CandidateIdentity("a1", "b2", "c3");
        var changedPatch = new CandidateIdentity("d4", "b2", "c3");
        var changedClosure = new CandidateIdentity("a1", "d4", "c3");
        var changedManifest = new CandidateIdentity("a1", "b2", "d4");

        Assert.Equal(first, same);
        Assert.False(CandidateIdentity.AreSameCandidate(first, changedPatch));
        Assert.False(CandidateIdentity.AreSameCandidate(first, changedClosure));
        Assert.False(CandidateIdentity.AreSameCandidate(first, changedManifest));
        Assert.False(CandidateIdentity.AreSameCandidate(first, null));
        Assert.Equal("candidate:v1:a1:b2:c3", first.Canonical);
        Assert.True(CandidateIdentity.TryParse(first.Canonical, out var parsed));
        Assert.Equal(first, parsed);
        Assert.Equal(first, JsonSerializer.Deserialize<CandidateIdentity>(JsonSerializer.Serialize(first)));
    }

    [Fact]
    public void DispatchAndVerdictKeepIdentityAcrossSnapshotRoundTrip()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Candidate identity round trip");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single(item => item.RequiredRole == AgentRole.Developer);
        var identity = new CandidateIdentity("a", "b", "c");
        kernel.ConfigureCandidateIdentityResolver(_ => identity);
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("worker", "command", "C:\\repo", DateTimeOffset.UtcNow));
        kernel.RecordTaskVerification(goal.Id, task.Id,
            new TaskVerificationRecord("command", "C:\\repo", 0, "ok", "", DateTimeOffset.UtcNow));

        Assert.Equal(identity, task.LastDispatch!.CandidateIdentity);
        Assert.Equal(identity, task.LastVerification!.CandidateIdentity);
        var restored = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
            JsonSerializer.Serialize(kernel.ExportSnapshot()))!);
        var restoredTask = restored.GetTask(goal.Id, task.Id);
        Assert.Equal(identity, restoredTask.LastDispatch!.CandidateIdentity);
        Assert.Equal(identity, restoredTask.LastVerification!.CandidateIdentity);
        Assert.Equal(identity, Assert.Single(restoredTask.VerificationHistory).CandidateIdentity);
    }
}

using System.Reflection;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: pure helper calls and read-only reflection; no hook mutation or external state.
public sealed class CliGoalReplacementExecutionTests
{
    [Fact]
    public void ObjectiveHash_NormalizesLineEndingsAndTrailingWhitespace()
    {
        var padded = CliGoalReplacementExecution.HashGoalReplacementObjective(
            "  Ship the slice  \r\nSecond line\t\r\n\r\n");
        var normalized = CliGoalReplacementExecution.HashGoalReplacementObjective(
            "Ship the slice\nSecond line");
        var changed = CliGoalReplacementExecution.HashGoalReplacementObjective(
            "Ship the slice\nSecond line.");

        Assert.Equal("17523d73e5062fa4644db8881fb92037a6760d1d6055f7f6442022eb3a440caa", padded);
        Assert.Equal(padded, normalized);
        Assert.Equal("94c6681d57506b3b200391028cbf0df2708141aa3947f6d4df6f1a9265e35970", changed);
        Assert.NotEqual(padded, changed);
    }

    [Fact]
    public void Fingerprint_KeepsItsMaterialDigest()
    {
        var automatic = CliGoalReplacementExecution.ComputeGoalReplacementFingerprint(
            predecessorGoalId: "goal-pred",
            backlogItemId: "item-1",
            dispositionMaterial: "ZeroWorkCorrection",
            reason: " Reason text \r\n",
            objectiveHash: "abc",
            coverage: SourceBacklogCoverage.Full,
            orderedRoles: "Planner,Developer",
            requestedPipeline: "auto",
            requestedAgentOverrides: string.Empty,
            assignedAgents: "unprepared");
        var fiveRole = CliGoalReplacementExecution.ComputeGoalReplacementFingerprint(
            predecessorGoalId: "goal-pred",
            backlogItemId: "item-1",
            dispositionMaterial: "ZeroWorkCorrection",
            reason: " Reason text \r\n",
            objectiveHash: "abc",
            coverage: SourceBacklogCoverage.Full,
            orderedRoles: "Planner,Developer",
            requestedPipeline: "five-role",
            requestedAgentOverrides: string.Empty,
            assignedAgents: "unprepared");

        Assert.Equal("2909b4df0e4c317673b97dfbf9f0f181f58b10f9e44a7e5e6fe07c738ce8e250", automatic);
        Assert.Equal("47cee8c9e704a340885e00c0a6d8bac20d10188927ca8efaa8ecafd28187c2ef", fiveRole);
        Assert.NotEqual(automatic, fiveRole);
    }

    [Fact]
    public void GoalReplacement_HasOneOwner()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        string[] methodNames =
        [
            "ExecuteGoalReplacementOutsideTransaction",
            "ThrowGoalReplacementTransferLeaseTimeout",
            "RenderGoalReplacementOutcomeCode",
            "BuildGoalReplacementFingerprint",
            "ComputeGoalReplacementFingerprint",
            "ComputeLegacyGoalReplacementFingerprint",
            "AcquireGoalReplacementTransferLease",
            "TryReplayCommittedGoalReplacement",
            "ThrowReplayedGoalReplacementFailure",
            "ComputeUnpreparedGoalReplacementFingerprint",
            "ResolveLegacyGoalReplacementReplayRoles",
            "TryRecordGoalReplacementFailure",
            "ResolveObservedClaimAfterReplacementFailure",
            "ResolveObservedClaim",
            "RecordLegacyOwnerAmbiguousFailure",
            "CreateUnavailableGoalReplacementFacts",
            "TryRecordGoalReplacementPreflightFailure",
            "TryGetGoalReplacementFlag",
            "GoalReplacementUnlinkedBacklogItemId",
            "NormalizeGoalReplacementPipelineRequest",
            "BuildGoalReplacementRequestedAgentOverrides",
            "TryParseGoalReplacementDisposition",
            "ExtractGoalReplacementFailureCode",
            "ResolveGoalReplacementPreflightFailureCode",
            "ResolveGoalReplacementPreflightRoles",
            "HashGoalReplacementObjective"
        ];
        string[] hookNames =
        [
            "AfterGoalReplacementLeaseAcquisitionFailed",
            "BeforeGoalReplacementFailureClaimObservation"
        ];
        string[] nestedTypeNames =
        [
            "GoalReplacementTransferLeaseAcquisitionKind",
            "GoalReplacementTransferLeaseAcquisition",
            "GoalReplacementValidationException"
        ];
        var movedNames = methodNames.Concat(hookNames).Concat(nestedTypeNames).ToHashSet(StringComparer.Ordinal);
        var runner = typeof(CliPersistentStateRunner);
        var execution = typeof(CliGoalReplacementExecution);

        Assert.DoesNotContain(runner.GetMembers(flags), member => movedNames.Contains(member.Name));
        Assert.True(execution.IsAbstract && execution.IsSealed);
        var methods = execution.GetMethods(flags);
        foreach (var name in methodNames)
        {
            Assert.Equal(name == "ComputeGoalReplacementFingerprint" ? 2 : 1,
                methods.Count(method => method.Name == name));
        }

        foreach (var name in hookNames)
        {
            var hook = Assert.Single(execution.GetProperties(flags), property => property.Name == name);
            Assert.All(hook.GetAccessors(nonPublic: true), accessor => Assert.True(accessor.IsStatic));
        }

        foreach (var name in nestedTypeNames)
        {
            Assert.Single(execution.GetNestedTypes(flags), type => type.Name == name);
        }

        Assert.True(Assert.Single(methods,
            method => method.Name == "ExecuteGoalReplacementOutsideTransaction").IsAssembly);
        var runnerMethods = runner.GetMethods(flags);
        Assert.Single(runnerMethods, method => method.Name == "ExecuteGoalCreateOutsideTransaction");
        Assert.True(Assert.Single(runnerMethods, method => method.Name == "ResolveCurrentGoal").IsAssembly);
        Assert.True(Assert.Single(runnerMethods,
            method => method.Name == "DeliverGoalCreationSideEffects").IsAssembly);
    }
}

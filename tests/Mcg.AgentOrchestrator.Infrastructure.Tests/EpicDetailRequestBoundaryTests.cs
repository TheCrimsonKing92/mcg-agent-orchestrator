using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: inspects the candidate assemblies; no global hooks or store access.
public sealed class EpicDetailRequestBoundaryTests
{
    [Fact]
    public void LoadDetail_RequestCallPaths_ExcludeBroadAcquisition()
    {
        var source = Method(typeof(WorkspaceEpicProgressSource), "LoadDetailAsync");
        var snapshot = Method(typeof(EpicDetailReadSnapshot), "ReadAsync");
        var calls = Reachable(source);
        // Prove the Task.Run delegate and async state machine were traversed.
        Assert.Contains(snapshot, calls);
        Assert.Contains(calls, method => method.DeclaringType == typeof(EpicPlanGoalClaimSnapshot)
            && method.Name == "ReadScoped");
        Assert.Empty(BroadAcquisitions(calls));
    }

    [Theory]
    [InlineData(nameof(ExtraMetadataRead), "EpicProgressReadModel.LoadGoalMetadata")]
    [InlineData(nameof(ExtraBacklogRead), "BoardFillBacklogSnapshot.Read")]
    public void Guard_DiscardedBroadReadOutsideTrace_DetectsAcquisition(string root, string forbidden)
    {
        // Compiled counterexamples, inspected rather than executed: either discarded
        // read opens an unobserved connection and leaves the SQL trace unchanged.
        var calls = Reachable(Method(typeof(EpicDetailRequestBoundaryTests), root));
        Assert.Contains(Method(typeof(EpicDetailReadSnapshot), "ReadAsync"), calls);
        Assert.Contains(forbidden, BroadAcquisitions(calls));
    }

    private static async Task<EpicDetailReadSnapshot> ExtraMetadataRead(OrchestratorWorkspace workspace)
    {
        var detail = await EpicDetailReadSnapshot.ReadAsync(workspace, "selected", null, CancellationToken.None);
        _ = EpicProgressReadModel.LoadGoalMetadata(workspace);
        return detail;
    }

    private static Task<EpicDetailReadSnapshot> ExtraBacklogRead(OrchestratorWorkspace workspace) =>
        Task.Run(async () =>
        {
            _ = BoardFillBacklogSnapshot.Read(workspace.BacklogStorePath);
            return await EpicDetailReadSnapshot.ReadAsync(workspace, "selected", null, CancellationToken.None);
        });

    private static string[] BroadAcquisitions(IEnumerable<MethodBase> calls) => calls
        .Where(method =>
            method.DeclaringType == typeof(EpicProgressReadModel) && method.Name is "Load" or "LoadGoalMetadata" ||
            method.DeclaringType == typeof(BoardFillBacklogSnapshot) && method.Name == "Read" ||
            method.DeclaringType == typeof(EpicPlanGoalClaimSnapshot) && method.Name == "Read" ||
            method.DeclaringType == typeof(EpicPlanStatusReader) && method.Name == "Load")
        .Select(method => method.DeclaringType!.Name + "." + method.Name).Distinct().ToArray();

    private static MethodInfo Method(Type type, string name) => type.GetMethod(name,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!;

    // Follow method operands (including ldftn delegates) and generated async bodies,
    // so a broad call through a helper cannot evade the request-boundary control.
    private static HashSet<MethodBase> Reachable(MethodBase root)
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opcode => unchecked((ushort)opcode.Value));
        var pending = new Stack<MethodBase>();
        var visited = new HashSet<MethodBase>();
        pending.Push(root);
        while (pending.TryPop(out var method))
        {
            if (!visited.Add(method)) continue;
            if (!method.Module.Assembly.GetName().Name!.StartsWith("Mcg.AgentOrchestrator", StringComparison.Ordinal)) continue;
            var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>();
            if (stateMachine is not null) pending.Push(Method(stateMachine.StateMachineType, "MoveNext"));
            var bytes = method.GetMethodBody()?.GetILAsByteArray();
            if (bytes is null) continue;
            var index = 0;
            while (index < bytes.Length)
            {
                var code = (ushort)bytes[index++];
                if (code == 0xfe) code = (ushort)(0xfe00 | bytes[index++]);
                var operand = opcodes[code].OperandType;
                if (operand == OperandType.InlineMethod)
                {
                    var called = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, index),
                        method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                    Assert.NotNull(called);
                    pending.Push(called);
                }
                index += operand switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, index),
                    OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
                        or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok
                        or OperandType.InlineType or OperandType.ShortInlineR => 4,
                    _ => throw new InvalidOperationException($"Unsupported IL operand {operand}")
                };
            }
        }
        return visited;
    }
}

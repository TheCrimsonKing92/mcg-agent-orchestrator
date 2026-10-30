using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: inspects candidate IL only; no global hooks, external processes or stores.
public sealed class ProjectOwnerAnswerRecorderBoundaryTests
{
    [Theory]
    [InlineData(typeof(ProjectAnswerCommand), "Execute")]
    [InlineData(typeof(OnboardingQuestionSource), "AnswerAsync")]
    [InlineData(typeof(OwnerDecisionWorkflow), "AnswerAsync")]
    public void AnswerEntryPoints_ReachSharedRecorder_WithoutBypassingValidationAndWrite(Type type, string name)
    {
        var calls = Reachable(Method(type, name));
        Assert.Contains(Method(typeof(ProjectOwnerAnswerRecorder), "Record"), calls);
        Assert.Empty(Bypasses(calls));
        if (type == typeof(OwnerDecisionWorkflow))
        {
            Assert.Contains(Method(typeof(OwnerOnboardingDecisionFlow), "RunAsync"), calls);
            Assert.Contains(Method(typeof(OnboardingQuestionSource), "AnswerAsync"), calls);
        }
    }

    [Fact]
    public void SharedRecorder_OwnsBothValidationAndPersistence()
    {
        var calls = Reachable(Method(typeof(ProjectOwnerAnswerRecorder), "Record"));
        Assert.Contains(calls, method => method.DeclaringType == typeof(OwnerAnswerValidator) && method.Name == "Validate");
        Assert.Contains(calls, method => method.DeclaringType == typeof(OwnerAnswerFile) && method.Name == "Write");
    }

    [Fact]
    public void BoundaryWalk_FollowsAsyncAndDelegateBodies_AndDetectsBypassAlongsideSharedCall()
    {
        // Inspect, never invoke: this compiled counterexample retains the shared call
        // while sneaking a validation and write into an async delegate.
        var calls = Reachable(Method(typeof(ProjectOwnerAnswerRecorderBoundaryTests), nameof(BypassingAnswer)));
        Assert.Contains(Method(typeof(ProjectOwnerAnswerRecorder), "Record"), calls);
        Assert.Equal(2, Bypasses(calls).Count());
    }

    private static async Task BypassingAnswer() => await Task.Run(() =>
    {
        _ = ProjectOwnerAnswerRecorder.Record("unused", "key", "value", null, DateTimeOffset.MinValue);
        _ = OwnerAnswerValidator.Validate("key", "value");
        OwnerAnswerFile.Write("unused", []);
    });

    private static IEnumerable<MethodBase> Bypasses(IEnumerable<MethodBase> calls) => calls.Where(method =>
        method.DeclaringType == typeof(OwnerAnswerValidator) && method.Name == "Validate" ||
        method.DeclaringType == typeof(OwnerAnswerFile) && method.Name == "Write");

    private static MethodInfo Method(Type type, string name) => type.GetMethod(name,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"Required candidate method missing: {type.Name}.{name}");

    // Stop at the shared boundary so its required writes cannot mask a caller bypass.
    // Follow generated async bodies, local functions and ldftn delegate operands.
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
            if (method != root && method.DeclaringType == typeof(ProjectOwnerAnswerRecorder)) continue;
            if (method.Module.Assembly != typeof(ProjectAnswerCommand).Assembly &&
                method.Module.Assembly != typeof(ProjectOwnerAnswerRecorderBoundaryTests).Assembly) continue;
            var machine = method.GetCustomAttribute<AsyncStateMachineAttribute>();
            if (machine is not null) pending.Push(Method(machine.StateMachineType, "MoveNext"));
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

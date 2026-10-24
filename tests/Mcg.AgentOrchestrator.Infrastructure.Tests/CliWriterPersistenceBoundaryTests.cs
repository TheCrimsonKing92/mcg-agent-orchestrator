using System.Reflection;
using System.Reflection.Emit;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: compiler-emitted method references only; no stores or global hooks.
public sealed class CliWriterPersistenceBoundaryTests
{
    private static readonly IReadOnlyDictionary<ushort, OpCode> Opcodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!).ToDictionary(opcode => unchecked((ushort)opcode.Value));

    [Xunit.Fact]
    public void Candidate_HasNoCallersOfCapturedGoalReplacement()
    {
        var sink = typeof(CliPersistentStateRunner).GetMethod("PersistSingleGoalSnapshot",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var callers = typeof(CliPersistentStateRunner).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)))
            .Where(method => References(method).Contains(sink))
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}").ToArray();
        Assert.True(callers.Length == 0,
            "Captured goal replacement is reachable from candidate code: " + string.Join(", ", callers));
    }

    [Xunit.Fact]
    public void Guard_DetectsCompiledRawCaptureDelegate()
    {
        var counterexample = typeof(CliWriterPersistenceBoundaryTests).GetMethod(nameof(RawCapture),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Contains(References(counterexample), method => method.DeclaringType == typeof(CliPersistentStateRunner) &&
            method.Name == "PersistSingleGoalSnapshot");
    }

    [Xunit.Fact]
    public void Candidate_CliCannotDirectlyInsertTasks()
    {
        var sinks = typeof(AgentOrchestratorKernel).GetMethods()
            .Where(method => method.Name is nameof(AgentOrchestratorKernel.AddTask) or nameof(AgentOrchestratorKernel.AddSliceBatchParentFixUpTask))
            .Cast<MethodBase>().ToHashSet();
        var bypasses = typeof(CliPersistentStateRunner).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)))
            .Where(method => References(method).Any(sinks.Contains)).ToArray();
        Assert.True(bypasses.Length == 0, "CLI task insertion must reach the typed inbox application owner: " +
            string.Join(", ", bypasses.Select(method => method.DeclaringType!.FullName + "." + method.Name)));
        var counterexample = typeof(CliWriterPersistenceBoundaryTests).GetMethod(nameof(DirectInsertion), BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Contains(References(counterexample), sinks.Contains);
    }

    private static void DirectInsertion(AgentOrchestratorKernel kernel, GoalId id) =>
        kernel.AddTask(id, AgentRole.Tester, "Compiled insertion bypass counterexample");

    // Inspected, never invoked. Keeps the boundary guard sensitive to the reverted-source mechanism.
    private static void RawCapture(ITransactionalOrchestratorStateRepository repository,
        AgentOrchestratorKernel kernel, GoalId id) =>
        CliPersistentStateRunner.PersistSingleGoalSnapshot(repository, kernel, id);

    private static IEnumerable<MethodBase> References(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null) yield break;
        for (var index = 0; index < bytes.Length;)
        {
            var code = (ushort)bytes[index++];
            if (code == 0xfe) code = (ushort)(0xfe00 | bytes[index++]);
            var operand = Opcodes[code].OperandType;
            if (operand == OperandType.InlineMethod)
            {
                var called = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, index),
                    method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (called is not null) yield return called;
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
}

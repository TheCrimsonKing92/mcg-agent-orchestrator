using System.Reflection;
using System.Reflection.Emit;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class FailedGoalRecoveryPolicyBoundaryTests
{
    private static readonly IReadOnlyDictionary<short, OpCode> OpCodes =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(code => code.Value);

    [Fact]
    public void EvaluationHasNoEffectCallbackOrEffectfulMemberBoundary()
    {
        var evaluate = typeof(FailedGoalRecoveryPolicy).GetMethod(nameof(FailedGoalRecoveryPolicy.Evaluate))!;

        Assert.DoesNotContain(evaluate.GetParameters(), parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
        var members = ReadReferencedMembersTransitive(evaluate);
        Assert.DoesNotContain(
            members.OfType<MethodBase>(),
            method => method.DeclaringType?.Assembly == evaluate.DeclaringType!.Assembly &&
                method.GetParameters().Any(parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType)));
        Assert.DoesNotContain(members, member => IsForbidden(member.DeclaringType));
    }

    private static bool IsForbidden(Type? type)
    {
        var name = type?.FullName ?? string.Empty;
        return name.StartsWith("System.IO.", StringComparison.Ordinal) ||
            name.StartsWith("System.Diagnostics.Process", StringComparison.Ordinal) ||
            name.StartsWith("System.Console", StringComparison.Ordinal) ||
            name.StartsWith("System.Threading.Thread", StringComparison.Ordinal) ||
            name.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal);
    }

    private static IReadOnlyList<MemberInfo> ReadReferencedMembersTransitive(MethodInfo root)
    {
        var assembly = root.DeclaringType!.Assembly;
        var pending = new Queue<MethodBase>([root]);
        var visited = new HashSet<MethodBase>();
        var members = new List<MemberInfo>();
        while (pending.TryDequeue(out var method))
        {
            if (!visited.Add(method))
                continue;

            foreach (var member in ReadReferencedMembers(method))
            {
                members.Add(member);
                if (member is MethodBase referenced && referenced.DeclaringType?.Assembly == assembly)
                    pending.Enqueue(referenced);
            }
        }

        return members;
    }

    private static IReadOnlyList<MemberInfo> ReadReferencedMembers(MethodBase method)
    {
        var body = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        var members = new List<MemberInfo>();
        for (var index = 0; index < body.Length;)
        {
            var first = body[index++];
            var value = first == 0xfe ? (short)(0xfe00 | body[index++]) : first;
            var opCode = OpCodes[value];
            if (opCode.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                var token = BitConverter.ToInt32(body, index);
                try
                {
                    members.Add(method.Module.ResolveMember(
                        token,
                        method.DeclaringType?.GetGenericArguments(),
                        method is MethodInfo methodInfo ? methodInfo.GetGenericArguments() : Type.EmptyTypes));
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidOperationException(
                        $"Could not resolve metadata token {token} while walking {method.DeclaringType?.FullName}.{method.Name}.",
                        ex);
                }
            }
            index += OperandSize(opCode, body, index);
        }
        return members;
    }

    private static int OperandSize(OpCode code, byte[] body, int index) => code.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod or
            OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or
            OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(body, index) * 4),
        _ => throw new InvalidOperationException($"Unsupported IL operand {code.OperandType}.")
    };
}

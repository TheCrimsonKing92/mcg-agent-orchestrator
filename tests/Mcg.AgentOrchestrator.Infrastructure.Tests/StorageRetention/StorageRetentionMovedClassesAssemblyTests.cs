using System.Reflection;

// Parallel-safe: inspects assembly metadata; the negative control filters an in-memory type list.
public sealed class StorageRetentionMovedClassesAssemblyTests
{
    [Theory]
    [InlineData("StorageRetentionMaintenanceTests")]
    [InlineData("StorageRetentionGoalJournalCompressionTests")]
    [InlineData("StorageRetentionResumeCursorTests")]
    [InlineData("StorageRetentionOwnerlessAttemptTests")]
    [InlineData("StorageRetentionTerminalGoalAttemptTests")]
    [InlineData("StorageRetentionOperatorLogTests")]
    public void MovedType_NestedAssembly_DeclaresTests(string typeName)
    {
        var assembly = typeof(StorageRetentionMovedClassesAssemblyTests).Assembly;

        Assert.Empty(Violations(assembly.GetName().Name, assembly.GetTypes(), typeName));
    }

    [Fact]
    public void MovedType_MissingFromAssembly_ReportsViolation()
    {
        var assembly = typeof(StorageRetentionMovedClassesAssemblyTests).Assembly;
        var types = assembly.GetTypes().Where(type => type.Name != "StorageRetentionMaintenanceTests").ToArray();

        Assert.Contains(Violations(assembly.GetName().Name, types, "StorageRetentionMaintenanceTests"),
            violation => violation.Contains("StorageRetentionMaintenanceTests", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> Violations(string? assemblyName, Type[] types, string typeName)
    {
        var violations = new List<string>();
        if (assemblyName != "Mcg.AgentOrchestrator.Infrastructure.StorageRetention.Tests")
            violations.Add($"Unexpected assembly: {assemblyName}.");

        var matches = types.Where(type => type.Name == typeName).ToArray();
        if (matches.Length != 1)
            violations.Add($"Expected one {typeName}, found {matches.Length}.");
        else if (!matches[0].GetMethods(BindingFlags.Public | BindingFlags.Instance |
                     BindingFlags.Static | BindingFlags.DeclaredOnly)
                 .Any(method => method.GetCustomAttributes(inherit: true).OfType<FactAttribute>().Any()))
            violations.Add($"{typeName} declares no public test methods.");

        return violations;
    }
}

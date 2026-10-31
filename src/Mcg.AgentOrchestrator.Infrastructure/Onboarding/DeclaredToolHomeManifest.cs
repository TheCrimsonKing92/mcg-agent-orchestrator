using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DeclaredToolHomeManifest
{
    internal static IReadOnlyList<DeclaredToolHome> Read(JsonElement root)
    {
        var property = root.EnumerateObject().FirstOrDefault(property =>
            property.Name.Equals("toolHomes", StringComparison.OrdinalIgnoreCase));
        if (property.Value.ValueKind == JsonValueKind.Undefined) return [];
        if (property.Value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Acceptance manifest toolHomes must be an array.");
        var homes = new List<DeclaredToolHome>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in property.Value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Acceptance manifest toolHomes must contain objects with variable and directory.");
            string? ReadString(string name) => entry.EnumerateObject()
                .FirstOrDefault(field => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value
                is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
            var variable = ReadString("variable");
            var directory = ReadString("directory");
            try
            {
                DeclaredToolHomeValidator.ValidateVariable(variable!);
                if (string.IsNullOrEmpty(directory) || !Path.IsPathFullyQualified(directory))
                    throw new ArgumentException($"Tool-home variable '{variable}' requires an absolute directory: '{directory}'.");
                if (!names.Add(variable!))
                    throw new ArgumentException($"Tool-home variable '{variable}' is declared more than once.");
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(exception.Message, exception);
            }
            homes.Add(new(variable!, directory!));
        }
        return homes.AsReadOnly();
    }
}

internal static class CliTestCollections
{
    internal const string ConsoleSerialized = "console-serialized";
}

// These tests either replace Console.Out or write directly to it. Keeping them in one
// collection prevents process-global console output from crossing capture boundaries.
[Xunit.CollectionDefinition(CliTestCollections.ConsoleSerialized)]
public sealed class ConsoleSerializedCollection;

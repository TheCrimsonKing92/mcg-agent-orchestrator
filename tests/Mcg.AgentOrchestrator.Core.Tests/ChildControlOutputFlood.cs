internal static class ChildControlOutputFlood
{
    internal const string EnvironmentVariable = "MCG_CORE_TEST_TEMP_CHILD_FLOOD_BYTES";
    internal const string OutputMarker = "STDOUT_FLOOD_END";
    internal const string ErrorMarker = "STDERR_FLOOD_END";

    internal static void WriteIfRequested()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (value is null) return;
        if (!int.TryParse(value, out var bytes) || bytes < 0)
            throw new InvalidOperationException($"Invalid {EnvironmentVariable}: {value}");
        Write(Console.Out, 'O', bytes, OutputMarker);
        Write(Console.Error, 'E', bytes, ErrorMarker);
    }

    private static void Write(TextWriter writer, char character, int bytes, string marker)
    {
        var block = new string(character, 8192);
        while (bytes >= block.Length)
        {
            writer.Write(block);
            bytes -= block.Length;
        }
        if (bytes > 0) writer.Write(block.AsSpan(0, bytes));
        writer.WriteLine(marker);
        writer.Flush();
    }
}

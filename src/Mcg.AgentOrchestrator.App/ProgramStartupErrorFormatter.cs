internal static class ProgramStartupErrorFormatter
{
    public static string Format(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message}";
}

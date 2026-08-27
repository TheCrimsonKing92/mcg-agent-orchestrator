var receiptPath = Environment.GetEnvironmentVariable("MCG_ISOLATED_DOTNET_MTP_PROBE_PATH");
if (string.IsNullOrWhiteSpace(receiptPath))
{
    return 2;
}

File.WriteAllLines(receiptPath, args);
return int.TryParse(
    Environment.GetEnvironmentVariable("MCG_ISOLATED_DOTNET_MTP_PROBE_EXIT_CODE"),
    out var exitCode)
    ? exitCode
    : 0;

var receiptPath = Environment.GetEnvironmentVariable("MCG_ISOLATED_DOTNET_MTP_PROBE_PATH");
if (string.IsNullOrWhiteSpace(receiptPath))
{
    return 2;
}

File.WriteAllLines(receiptPath, args);
return 0;

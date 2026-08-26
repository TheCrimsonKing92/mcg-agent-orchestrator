using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class AcceptanceGateFlakeInventoryGeneratorTests
{
    [Xunit.Fact]
    public void Generator_ValidReceipts_WritesGroupedSupplement()
    {
        using var fixture = GeneratorFixture.Create();
        fixture.WriteEvidence(expectedReceipts: 2);

        var first = fixture.Run();
        var second = fixture.Run();

        Xunit.Assert.True(first.ExitCode == 0, first.Stdout + first.Stderr);
        Xunit.Assert.True(second.ExitCode == 0, second.Stdout + second.Stderr);
        Xunit.Assert.Contains("status=updated receipts=2 passed=1 failed=1 groups=1", first.Stdout, StringComparison.Ordinal);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(first.Stderr), first.Stderr);

        var document = File.ReadAllText(fixture.DocumentPath);
        Xunit.Assert.Contains("Input counts after receipt-identity deduplication: 2 attempts; 1 pass; 1 fail", document, StringComparison.Ordinal);
        Xunit.Assert.Contains("1 pass/1 fail", document, StringComparison.Ordinal);
        Xunit.Assert.Contains("| ExampleTests.IntermittentCase | 2 | 1 | 1 | 50.00% | 1 |", document, StringComparison.Ordinal);
        Xunit.Assert.Contains("1x `git init failed (0):`", document, StringComparison.Ordinal);
        Xunit.Assert.Contains("mechanism-undetermined", document, StringComparison.Ordinal);
        Xunit.Assert.Equal(1, CountOccurrences(document, "acceptance-gate-flake-inventory-supplements:BEGIN"));
        Xunit.Assert.Equal(1, CountOccurrences(document, "acceptance-gate-flake-inventory-supplements:END"));
    }

    [Xunit.Fact]
    public void Generator_IncompleteCorpus_FailsClosedWithoutChangingDocument()
    {
        using var fixture = GeneratorFixture.Create();
        fixture.WriteEvidence(expectedReceipts: 3);
        var before = SHA256.HashData(File.ReadAllBytes(fixture.DocumentPath));

        var result = fixture.Run();

        Xunit.Assert.Equal(2, result.ExitCode);
        Xunit.Assert.Contains("receipt count mismatch: expected=3 actual=2", result.Stderr, StringComparison.Ordinal);
        Xunit.Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(fixture.DocumentPath)));
    }

    [Xunit.Fact]
    public void Generator_MalformedEvidence_FailsClosedWithoutChangingDocument()
    {
        using var fixture = GeneratorFixture.Create();
        File.WriteAllText(fixture.EvidencePath, "{\"contractVersion\":");
        var before = SHA256.HashData(File.ReadAllBytes(fixture.DocumentPath));

        var result = fixture.Run();

        Xunit.Assert.Equal(2, result.ExitCode);
        Xunit.Assert.Contains("evidence is not valid JSON", result.Stderr, StringComparison.Ordinal);
        Xunit.Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(fixture.DocumentPath)));
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }
        return count;
    }

    private sealed class GeneratorFixture : IDisposable
    {
        private GeneratorFixture(string root, string scriptPath)
        {
            Root = root;
            ScriptPath = scriptPath;
            EvidencePath = Path.Combine(root, "receipts.json");
            DocumentPath = Path.Combine(root, "inventory.md");
            File.WriteAllText(
                DocumentPath,
                "# Acceptance-gate flake inventory\r\n\r\n" +
                "Generated fixture.\r\n\r\n" +
                "## Reproduction commands\r\n\r\nFixture body.\r\n");
        }

        public string Root { get; }
        public string ScriptPath { get; }
        public string EvidencePath { get; }
        public string DocumentPath { get; }

        public static GeneratorFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "mcg-flake-inventory-generator-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var scriptPath = Path.Combine(
                InfrastructureTestSupport.FindRepositoryRoot(),
                "scripts",
                "Update-AcceptanceGateFlakeInventory.ps1");
            return new GeneratorFixture(root, scriptPath);
        }

        public void WriteEvidence(int expectedReceipts)
        {
            var testName = "ExampleTests.IntermittentCase";
            var evidence = new
            {
                contractVersion = 1,
                source = "Focused synthetic retained-receipt extract.",
                corpus = new
                {
                    goalIds = new[] { "aaaaaaaa11111111bbbbbbbb22222222" },
                    expectedReceiptCount = expectedReceipts,
                    expectedPassCount = 1,
                    expectedFailureCount = expectedReceipts - 1
                },
                receipts = new object[]
                {
                    new
                    {
                        receiptIdentity = "attempt-pass",
                        goalId = "aaaaaaaa11111111bbbbbbbb22222222",
                        timestampUtc = "2026-08-24T01:00:00Z",
                        testName,
                        outcome = "passed"
                    },
                    new
                    {
                        receiptIdentity = "attempt-fail",
                        goalId = "aaaaaaaa11111111bbbbbbbb22222222",
                        timestampUtc = "2026-08-24T02:00:00Z",
                        testName,
                        outcome = "failed",
                        signature = "git init failed (0):"
                    }
                }
            };
            File.WriteAllText(EvidencePath, JsonSerializer.Serialize(evidence));
        }

        public ProcessResult Run()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = Root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(ScriptPath);
            startInfo.ArgumentList.Add("-EvidencePath");
            startInfo.ArgumentList.Add(EvidencePath);
            startInfo.ArgumentList.Add("-DocumentPath");
            startInfo.ArgumentList.Add(DocumentPath);
            startInfo.ArgumentList.Add("-ScanTimestampUtc");
            startInfo.ArgumentList.Add("2026-08-26T14:00:00Z");

            var path = Environment.GetEnvironmentVariable("PATH");
            var systemRoot = Environment.GetEnvironmentVariable("SYSTEMROOT");
            startInfo.Environment.Clear();
            if (!string.IsNullOrWhiteSpace(path)) startInfo.Environment["PATH"] = path;
            if (!string.IsNullOrWhiteSpace(systemRoot))
            {
                startInfo.Environment["SYSTEMROOT"] = systemRoot;
                startInfo.Environment["WINDIR"] = systemRoot;
            }
            startInfo.Environment["TEMP"] = Root;
            startInfo.Environment["TMP"] = Root;

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to launch inventory generator fixture.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            Xunit.Assert.True(process.WaitForExit(30_000), "Inventory generator exceeded its 30-second failsafe.");
            return new ProcessResult(
                process.ExitCode,
                stdout.GetAwaiter().GetResult(),
                stderr.GetAwaiter().GetResult());
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}

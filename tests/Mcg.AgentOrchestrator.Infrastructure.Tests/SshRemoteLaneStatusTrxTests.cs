using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SshRemoteLaneStatusTrxTests
{
    // Executor fields quoted by the objective's recorded ledger premise; unrelated fields omitted.
    [Xunit.Theory]
    [Xunit.InlineData("""{"state":"completed","exitCode":29,"seconds":1051,"trx":[]}""", 29)]
    [Xunit.InlineData("""{"state":"completed","exitCode":26,"seconds":155,"trx":[]}""", 26)]
    [Xunit.InlineData("""{"state":"completed","exitCode":24,"seconds":103,"trx":[]}""", 24)]
    public void RecordedExecutorFailureKeepsExitWithoutTrx(string json, int expectedExit)
    {
        var (exit, names) = Parse(json).Completion();
        Assert.Equal(expectedExit, exit);
        Assert.Empty(names);
    }

    [Xunit.Theory]
    [Xunit.InlineData("""{"exitCode":0,"trx":[]}""", "no-trx-produced exitCode=0")]
    [Xunit.InlineData("""{"exitCode":0}""", "missing-trx")]
    [Xunit.InlineData("""{"exitCode":29}""", "missing-trx")]
    [Xunit.InlineData("""{"trx":[]}""", "missing-or-invalid-exitCode")]
    [Xunit.InlineData("""{"exitCode":"29","trx":[]}""", "missing-or-invalid-exitCode")]
    [Xunit.InlineData("""{"exitCode":2147483648,"trx":[]}""", "missing-or-invalid-exitCode")]
    [Xunit.InlineData("""{"exitCode":29,"trx":[17]}""", "invalid-trx-name: non-string")]
    [Xunit.InlineData("""{"exitCode":29,"trx":[""]}""", "invalid-trx-name: ")]
    public void RejectsIncompleteOrInvalidStatus(string json, string expected)
    {
        Assert.Equal(expected, Assert.Throws<RemoteLaneTransportException>(() => Parse(json).Completion()).Message);
    }

    [Xunit.Theory]
    [Xunit.InlineData("null")]
    [Xunit.InlineData("17")]
    [Xunit.InlineData("{}")]
    [Xunit.InlineData("true")]
    [Xunit.InlineData("false")]
    public void NonListValuesStayRejectedForBothExitKinds(string trxJson)
    {
        foreach (var exit in new[] { 0, 29 })
            Assert.Equal("invalid-trx-name: non-string", Assert.Throws<RemoteLaneTransportException>(
                () => Parse($"{{\"exitCode\":{exit},\"trx\":{trxJson}}}").Completion()).Message);
    }

    [Xunit.Theory]
    [Xunit.InlineData("\"one.trx\"", new[] { "one.trx" })]
    [Xunit.InlineData("[\"one.trx\",\"two.trx\"]", new[] { "one.trx", "two.trx" })]
    public void ValidStringAndArrayKeepNamesAndOrder(string trxJson, string[] expected)
    {
        var (exit, names) = Parse($"{{\"exitCode\":0,\"trx\":{trxJson}}}").Completion();
        Assert.Equal(0, exit);
        Assert.Equal(expected, names);
    }

    [Xunit.Theory]
    [Xunit.InlineData("../unsafe.trx", "..?unsafe.trx")]
    [Xunit.InlineData(@"a\b.trx", "a?b.trx")]
    [Xunit.InlineData("a/b.trx", "a?b.trx")]
    [Xunit.InlineData("ok.trx\nx", "ok.trx?x")]
    [Xunit.InlineData("ok.trx\n", "ok.trx?")]
    [Xunit.InlineData("a \"b.trx", "a ?b.trx")]
    [Xunit.InlineData("no-extension", "no-extension")]
    public void UnsafeNameIsRejectedWithSafeValue(string name, string expected)
    {
        var error = Assert.Throws<RemoteLaneTransportException>(() => WithNames(new[] { name }).Completion());
        Assert.StartsWith("invalid-trx-name: ", error.Message);
        Assert.Equal("invalid-trx-name: " + expected, error.Message);
        Assert.DoesNotContain(error.Message, character => character is '\r' or '\n' or '/' or '\\' or '"');
    }

    [Xunit.Fact]
    public void LongInvalidNameHasBoundedValue()
    {
        var error = Assert.Throws<RemoteLaneTransportException>(() => WithNames(new[] { new string('a', 300) }).Completion());
        const string prefix = "invalid-trx-name: ";
        Assert.StartsWith(prefix, error.Message);
        var value = error.Message[prefix.Length..];
        Assert.InRange(value.Length, 0, 83);
        Assert.Equal(new string('a', 80) + "...", value);
    }

    [Xunit.Theory]
    [Xunit.InlineData("A_z-09.test.trx")]
    [Xunit.InlineData("..trx")]
    public void ValidNamesAreNotSanitized(string name)
    {
        Assert.Equal(name, RemoteLaneStatusValueText.Sanitize(name));
        Assert.Equal(new[] { name }, WithNames(new[] { name }).Completion().Names);
    }

    [Xunit.Fact]
    public void ValidLongNameIsAcceptedWithoutTruncation()
    {
        var name = new string('a', 300) + ".trx";
        Assert.Equal(new[] { name }, WithNames(new[] { name }).Completion().Names);
    }

    [Xunit.Theory]
    [Xunit.InlineData("[\"one.trx\",\"../first.trx\",17]", "invalid-trx-name: ..?first.trx")]
    [Xunit.InlineData("[\"one.trx\",17,\"../second.trx\"]", "invalid-trx-name: non-string")]
    public void ReportsFirstOffendingElement(string trxJson, string expected)
    {
        Assert.Equal(expected, Assert.Throws<RemoteLaneTransportException>(
            () => Parse($"{{\"exitCode\":29,\"trx\":{trxJson}}}").Completion()).Message);
    }

    private static SshRemoteLaneStatus WithNames(string[] names) =>
        Parse(JsonSerializer.Serialize(new { exitCode = 29, trx = names }));

    private static SshRemoteLaneStatus Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new(document.RootElement.Clone());
    }
}

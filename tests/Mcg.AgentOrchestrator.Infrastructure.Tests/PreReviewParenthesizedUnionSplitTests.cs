using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: all inputs and outputs are local, in-memory filter strings.
public sealed class PreReviewParenthesizedUnionSplitTests
{
    private const string Alias = "Infrastructure.Tests";
    private const int BrokerLimit = 1024;

    [Fact]
    public void FortyClauseParenthesizedUnion_SplitsIntoBrokerSafeItemsWithExactCoverage()
    {
        var clauses = Clauses();
        var offset = 0;
        var groups = new[] { 2, 7, 1, 13, 17 }.Select(size =>
        {
            var group = $"({string.Join('|', clauses.Skip(offset).Take(size))})";
            offset += size;
            return group;
        }).ToArray();
        var filter = string.Join('|', groups);
        Assert.True(1 + filter.Length > BrokerLimit);

        Assert.True(PreReviewFocusedRequestSplitter.TrySplitRequestItems(Alias, filter, out var items));

        Assert.True(items.Count > 1);
        AssertBrokerSafe(items);
        var emitted = ReadClauses(items);
        Assert.Equal(40, emitted.Length);
        Assert.Equal(clauses, emitted);
        Assert.Equal(40, emitted.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ParenthesizedUnionDuplicates_DeduplicateCaseInsensitivelyInFirstSeenOrder()
    {
        var clauses = Clauses();
        clauses[1] = "FullyQualifiedName  ~  ParenthesizedUnionClass02Tests";
        var filter = $"({string.Join('|', clauses.Take(2))})|" +
            $"( FullyQualifiedName~parenthesizedunionclass02tests |{string.Join('|', clauses.Skip(2))})|" +
            " FullyQualifiedName ~ PARENTHESIZEDUNIONCLASS01TESTS ";
        Assert.True(1 + filter.Length > BrokerLimit);

        Assert.True(PreReviewFocusedRequestSplitter.TrySplitRequestItems(Alias, filter, out var items));

        AssertBrokerSafe(items);
        Assert.Equal(clauses, ReadClauses(items));
    }

    [Theory]
    [InlineData("one-group")]
    [InlineData("mixed-operands")]
    public void SupportedOversizedGrouping_PreservesAllClauses(string shape)
    {
        var clauses = Clauses();
        var filter = shape == "one-group"
            ? $"({string.Join('|', clauses)})"
            : $" {clauses[0]} | ({clauses[1]}) | ({string.Join('|', clauses.Skip(2))}) ";
        Assert.True(1 + filter.Length > BrokerLimit);

        Assert.True(PreReviewFocusedRequestSplitter.TrySplitRequestItems(Alias, filter, out var items));

        AssertBrokerSafe(items);
        Assert.Equal(clauses, ReadClauses(items));
    }

    [Fact]
    public void DuplicateHeavyOversizedGroup_ReturnsOneNormalizedItem()
    {
        const string first = "FullyQualifiedName ~ FirstTests";
        const string second = "FullyQualifiedName~SecondTests";
        var filter = $"({first}|{second}|" +
            string.Join('|', Enumerable.Repeat("FullyQualifiedName~firsttests", 50)) + ")";
        Assert.True(1 + filter.Length > BrokerLimit);

        Assert.True(PreReviewFocusedRequestSplitter.TrySplitRequestItems(Alias, filter, out var items));

        Assert.Equal($"{Alias}:{first}|{second}", Assert.Single(items));
        AssertBrokerSafe(items);
    }

    [Theory]
    [InlineData("conjunction")]
    [InlineData("negation")]
    [InlineData("nested")]
    [InlineData("group-conjunction")]
    [InlineData("empty-group")]
    [InlineData("unclosed-group")]
    [InlineData("extra-close")]
    [InlineData("trailing-union")]
    [InlineData("empty-operand")]
    [InlineData("trailing-token")]
    [InlineData("invalid-identifier")]
    public void UnsupportedOversizedShapes_StillRefuseToSplit(string shape)
    {
        var clauses = Clauses();
        var union = string.Join('|', clauses);
        var filter = shape switch
        {
            "conjunction" => $"({union})&FullyQualifiedName~Required",
            "negation" => $"({union})|FullyQualifiedName!~Excluded",
            "nested" => $"(({clauses[0]}|{clauses[1]})|{clauses[2]})|({string.Join('|', clauses.Skip(3))})",
            "group-conjunction" => $"({clauses[0]}&{clauses[1]})|({string.Join('|', clauses.Skip(2))})",
            "empty-group" => $"({union})|()",
            "unclosed-group" => $"({union}",
            "extra-close" => $"({union}))",
            "trailing-union" => $"({union})|",
            "empty-operand" => $"({union})||FullyQualifiedName~Other",
            "trailing-token" => $"({union})FullyQualifiedName~Other",
            "invalid-identifier" => $"({union})|FullyQualifiedName~123Invalid",
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        Assert.True(1 + filter.Length > BrokerLimit);

        Assert.False(PreReviewFocusedRequestSplitter.TrySplitRequestItems(Alias, filter, out var items));

        Assert.Equal(new[] { $"{Alias}: {filter}" }, items);
    }

    [Theory]
    [InlineData("flat")]
    [InlineData("parenthesized")]
    [InlineData("duplicates")]
    [InlineData("unsupported")]
    [InlineData("boundary-flat")]
    [InlineData("boundary-parenthesized")]
    public void ShortFilters_ReturnSingleOriginalItemUnchanged(string shape)
    {
        var filter = shape switch
        {
            "flat" => "FullyQualifiedName~A|FullyQualifiedName~B",
            "parenthesized" => "(FullyQualifiedName~A|FullyQualifiedName~B)",
            "duplicates" => " (FullyQualifiedName~A|FullyQualifiedName~B)|(FullyQualifiedName~A) ",
            "unsupported" => "(FullyQualifiedName~A&FullyQualifiedName~B)",
            "boundary-flat" => "FullyQualifiedName~A|FullyQualifiedName~B",
            "boundary-parenthesized" => "(FullyQualifiedName~A|FullyQualifiedName~B)|(FullyQualifiedName~C)",
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        if (shape.StartsWith("boundary-", StringComparison.Ordinal))
        {
            filter = filter.Replace("~A", "~" + new string('A', BrokerLimit - filter.Length));
            Assert.Equal(BrokerLimit, 1 + filter.Length);
        }
        Assert.True(1 + filter.Length <= BrokerLimit);

        Assert.True(PreReviewFocusedRequestSplitter.TrySplitRequestItems(Alias, filter, out var items));

        Assert.Equal(new[] { $"{Alias}: {filter}" }, items);
    }

    [Fact]
    public void IndivisibleOversizedGroupedClause_ReturnsOriginalItemAndFalse()
    {
        var filter = $"(FullyQualifiedName~{new string('A', BrokerLimit)})";

        Assert.False(PreReviewFocusedRequestSplitter.TrySplitRequestItems(Alias, filter, out var items));

        Assert.Equal(new[] { $"{Alias}: {filter}" }, items);
    }

    private static string[] Clauses() => Enumerable.Range(1, 40)
        .Select(index => $"FullyQualifiedName~ParenthesizedUnionClass{index:D2}Tests")
        .ToArray();

    private static void AssertBrokerSafe(IReadOnlyList<string> items)
    {
        Assert.NotEmpty(items);
        Assert.All(items, item =>
        {
            Assert.StartsWith($"{Alias}:", item);
            var expression = item[(item.IndexOf(':') + 1)..];
            Assert.InRange(expression.Length, 1, BrokerLimit);
            Assert.DoesNotContain("(", expression);
            Assert.DoesNotContain(")", expression);
        });
    }

    private static string[] ReadClauses(IReadOnlyList<string> items) => items
        .SelectMany(item => item[(item.IndexOf(':') + 1)..].Trim().Split('|'))
        .ToArray();
}

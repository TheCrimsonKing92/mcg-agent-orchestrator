using System.Text.RegularExpressions;

internal static class MtpFilterArgumentSemantics
{
    private static readonly string[] KnownOptions =
    [
        "--filter-class", "--filter-not-class", "--filter-method", "--filter-not-method", "--filter-not-trait"
    ];

    internal static IReadOnlyList<MtpTestRunnerScriptTests.ManagedTestDescriptor> Select(
        IReadOnlyList<string> arguments,
        IReadOnlyList<MtpTestRunnerScriptTests.ManagedTestDescriptor> catalog)
    {
        var options = Parse(arguments);
        var includesClass = options.Where(option => option.Kind == "--filter-class").ToArray();
        var includesMethod = options.Where(option => option.Kind == "--filter-method").ToArray();
        var excludes = options.Where(option => option.Kind is not ("--filter-class" or "--filter-method")).ToArray();
        return catalog.Where(test =>
            (includesClass.Length == 0 || includesClass.Any(option => Matches(test.TypeName, option.Value))) &&
            (includesMethod.Length == 0 || includesMethod.Any(option => Matches(test.TypeName + "." + test.MethodName, option.Value))) &&
            excludes.All(option => !MatchesOption(test, option)))
            .ToArray();
    }

    internal static string KindSignature(IReadOnlyList<string> arguments)
    {
        var options = Parse(arguments);
        var kinds = options.GroupBy(option => option.Kind, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key + (group.Count() > 1 ? "+" : ""));
        var includes = options.Where(option => option.Kind == "--filter-class")
            .Select(option => option.Value.Trim('*'));
        var nested = options.Where(option => option.Kind == "--filter-not-class")
            .Any(option => includes.Any(value => option.Value.Trim('*').Contains(value, StringComparison.OrdinalIgnoreCase)));
        return string.Join("|", kinds) + (nested ? "|nested" : "");
    }

    internal static IReadOnlyList<string> UncoveredSignatures(
        IEnumerable<string> required,
        IEnumerable<string> validated) => required.Except(validated, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    internal static bool IsNonVacuous(
        IReadOnlyList<string> arguments,
        IReadOnlyList<MtpTestRunnerScriptTests.ManagedTestDescriptor> catalog)
    {
        var options = Parse(arguments);
        var includesClass = options.Where(option => option.Kind == "--filter-class").ToArray();
        var includesMethod = options.Where(option => option.Kind == "--filter-method").ToArray();
        var positivelySelected = catalog.Where(test =>
            (includesClass.Length == 0 || includesClass.Any(option => Matches(test.TypeName, option.Value))) &&
            (includesMethod.Length == 0 || includesMethod.Any(option => Matches(test.TypeName + "." + test.MethodName, option.Value)))).ToArray();
        if (positivelySelected.Length == 0)
        {
            return false;
        }
        return options.Where(option => option.Kind.StartsWith("--filter-not-", StringComparison.Ordinal))
            .GroupBy(option => option.Kind, StringComparer.Ordinal)
            .All(group => group.Any(option => positivelySelected.Any(test => MatchesOption(test, option))));
    }

    private static (string Kind, string Value)[] Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count % 2 != 0)
        {
            throw new InvalidOperationException("MTP filter arguments must contain option/value pairs.");
        }
        var options = new (string Kind, string Value)[arguments.Count / 2];
        for (var index = 0; index < arguments.Count; index += 2)
        {
            var kind = arguments[index];
            if (!KnownOptions.Contains(kind, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Unknown MTP filter option: {kind}");
            }
            options[index / 2] = (kind, arguments[index + 1]);
        }
        return options;
    }

    private static bool MatchesOption(MtpTestRunnerScriptTests.ManagedTestDescriptor test, (string Kind, string Value) option)
        => option.Kind switch
        {
            "--filter-class" or "--filter-not-class" => Matches(test.TypeName, option.Value),
            "--filter-method" or "--filter-not-method" => Matches(test.TypeName + "." + test.MethodName, option.Value),
            "--filter-not-trait" => MatchesTrait(test, option.Value),
            _ => throw new InvalidOperationException($"Unknown MTP filter option: {option.Kind}")
        };

    private static bool MatchesTrait(MtpTestRunnerScriptTests.ManagedTestDescriptor test, string value)
    {
        var separator = value.IndexOf('=');
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new InvalidOperationException($"Malformed MTP trait filter: {value}");
        }
        return test.Traits.Any(trait =>
            Matches(trait.Key, value[..separator]) && Matches(trait.Value, value[(separator + 1)..]));
    }

    private static bool Matches(string candidate, string pattern)
    {
        if (pattern.Length >= 2 && pattern[0] == '*' && pattern[^1] == '*' &&
            pattern[1..^1].IndexOf('*') < 0)
        {
            return candidate.Contains(pattern[1..^1], StringComparison.OrdinalIgnoreCase);
        }
        if (pattern.IndexOf('*') < 0)
        {
            return candidate.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }
        return Regex.IsMatch(candidate,
            "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

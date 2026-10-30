namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PreReviewPositiveUnionNormalizer
{
    internal static bool TryFlatten(string filterText, out string flattened)
    {
        // Publish a rewrite only after every operand satisfies the positive union grammar.
        flattened = filterText;
        var clauses = new List<string>();
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var position = 0;
        while (true)
        {
            while (position < filterText.Length && char.IsWhiteSpace(filterText[position]))
            {
                position++;
            }

            if (position == filterText.Length)
            {
                return false;
            }

            string operand;
            if (filterText[position] == '(')
            {
                var close = filterText.IndexOf(')', position + 1);
                if (close < 0)
                {
                    return false;
                }

                operand = filterText[(position + 1)..close];
                position = close + 1;
            }
            else
            {
                var separator = filterText.IndexOf('|', position);
                var end = separator < 0 ? filterText.Length : separator;
                operand = filterText[position..end];
                position = end;
            }

            foreach (var part in operand.Split('|'))
            {
                var clause = part.Trim();
                if (!PreReviewFocusedRequestSplitter.IsPositiveClause(clause))
                {
                    return false;
                }

                var identifier = clause[(clause.IndexOf('~') + 1)..].Trim();
                if (identifiers.Add(identifier))
                {
                    clauses.Add(clause);
                }
            }

            while (position < filterText.Length && char.IsWhiteSpace(filterText[position]))
            {
                position++;
            }

            if (position == filterText.Length)
            {
                flattened = string.Join('|', clauses);
                return true;
            }

            if (filterText[position++] != '|')
            {
                return false;
            }
        }
    }
}

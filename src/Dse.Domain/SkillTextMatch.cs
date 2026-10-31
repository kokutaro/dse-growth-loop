namespace Dse.Domain;

// Literal retrieval inside already grounded skill claims. No sentence splitting,
// synonym generation, or changes to the stored source/claim/experience decision.
public static class SkillTextMatch
{
    public static bool AllowsPartial(string query) => query.EnumerateRunes().Count() >= 2 && query.Length <= 100;
    private static bool Identifier(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '+' or '#' or '.';
    public static bool Matches(string term, string query)
    {
        term = SpecificSkillRules.Normalize(term); query = SpecificSkillRules.Normalize(query);
        if (term == query) return true;
        if (!AllowsPartial(query)) return false;
        int start = 0;
        while (start <= term.Length - query.Length)
        {
            var at = term.IndexOf(query, start, StringComparison.Ordinal);
            if (at < 0) return false;
            var end = at + query.Length;
            if ((!Identifier(query[0]) || at == 0 || !Identifier(term[at - 1])) &&
                (!Identifier(query[^1]) || end == term.Length || !Identifier(term[end]))) return true;
            start = at + 1;
        }
        return false;
    }
}

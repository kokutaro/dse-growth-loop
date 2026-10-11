using System.Text.RegularExpressions;
namespace Dse.Domain;

// A bounded defence for known literals and obvious email/ID shapes, not semantic anonymization.
public static class AnonymousWorkspaceKnownIdentifiers
{
    public static bool Contains(string? text,IEnumerable<string> known)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return Regex.IsMatch(text,@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}",RegexOptions.IgnoreCase) ||
            Regex.IsMatch(text,@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",RegexOptions.IgnoreCase) ||
            Regex.IsMatch(text,@"(?<![A-Z0-9_])(?:demo-)?(?:principal|person|session|department|dep|employee|participant|owner|challenge|knowledge)-[A-Z0-9][A-Z0-9_-]*",RegexOptions.IgnoreCase) ||
            known.Where(x => !string.IsNullOrWhiteSpace(x) && x.Length > 1).Any(x => text.Contains(x,StringComparison.OrdinalIgnoreCase));
    }
    public static void RequireSafe(IEnumerable<string?> texts,IEnumerable<string> known)
    { var identifiers = known.Distinct().ToArray(); if (texts.Any(t => Contains(t,identifiers))) throw new DomainException(422,"IDENTIFYING_CONTENT"); }
}

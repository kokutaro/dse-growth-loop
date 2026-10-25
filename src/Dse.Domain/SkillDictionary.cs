using System.Text.Json;
using System.Text;

namespace Dse.Domain;

public sealed record SkillAlias(string Text, string Kind, string? Object, string[] Conditions);
public sealed record SkillConcept(string Id, string Name, string Definition, string Boundary, string[] ScopeIds,
    string State, string? RedirectId, int IntroducedVersion, SkillAlias[] Aliases);
public sealed record SkillDictionarySnapshot(int Version, SkillConcept[] Concepts);
public sealed record SkillDictionaryReview(string Action, string Reason, int ExpectedVersion, string DictionaryETag,
    string? TargetId = null, string? SourceId = null, string? Name = null, string? Definition = null, string? Boundary = null);
public sealed record SkillCandidateCollect(string FragmentId);
public sealed record SkillDictionaryPreview(int Version, string[] CandidateIds);
public sealed record SkillDictionaryMatch(string State, string? ConceptId);

// Exact, human-approved aliases only. The occurrence's role, subject and source
// remain separate; mapping to a concept never recognizes a person's experience.
public static class SkillDictionaryRules
{
    public const string Version = "growing-skill-dictionary-v1";
    public const int MaxConcepts = 200;
    public static SkillAlias Alias(SkillElement e) => new(e.Label, e.Kind, e.Object, e.Conditions);
    // Spelling only. Do not inherit the legacy extractor's semantic aliases:
    // each dictionary alias must be present in the approved version itself.
    private static string Normalize(string s) => s.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
    public static string AliasKey(SkillAlias a) => JsonSerializer.Serialize(new {
        text=Normalize(a.Text),a.Kind, target=a.Object is null?null:Normalize(a.Object),
        conditions=a.Conditions.Select(Normalize).Distinct().Order(StringComparer.Ordinal).ToArray() });
    public static bool SameScopes(string[] a,string[] b) => a.ToHashSet(StringComparer.Ordinal).SetEquals(b);
    public static SkillDictionaryMatch Match(SkillDictionarySnapshot snapshot, SkillElement element, string[] scopes)
    {
        var key=AliasKey(Alias(element));
        var found=snapshot.Concepts.Where(c=>c.State=="active" && SameScopes(c.ScopeIds,scopes) && c.Aliases.Any(a=>AliasKey(a)==key)).ToArray();
        return found.Length switch {0=>new("unregistered",null),1=>new("mapped",found[0].Id),_=>new("undetermined",null)};
    }
    public static SkillConcept Resolve(SkillDictionarySnapshot snapshot,string id)
    {
        var seen=new HashSet<string>();
        while(true)
        {
            if(!seen.Add(id))throw new ArgumentException("Dictionary redirect cycle");
            var c=snapshot.Concepts.SingleOrDefault(c=>c.Id==id)??throw new DomainException(404,"NOT_FOUND");
            if(c.State=="active")return c;
            id=c.RedirectId??throw new ArgumentException("Missing redirect");
        }
    }
    public static SkillDictionarySnapshot Approve(SkillDictionarySnapshot before, SkillDictionaryReview review,
        SkillElement element,string[] scopes,string newId)
    {
        if(review.Action is not ("alias" or "new" or "merge"))return before;
        var next=checked(before.Version+1);var concepts=before.Concepts.ToList();var alias=Alias(element);
        void Text(string? s,int max){if(string.IsNullOrWhiteSpace(s)||s.Length>max)throw new ArgumentException("Definition and boundary required");}
        SkillConcept Target(string? id)
        {
            var c=concepts.SingleOrDefault(c=>c.Id==id&&c.State=="active")??throw new DomainException(404,"NOT_FOUND");
            if(!SameScopes(c.ScopeIds,scopes))throw new DomainException(409,"SKILL_SCOPE_MISMATCH");return c;
        }
        void Unique(IEnumerable<SkillAlias> aliases,params string[] except)
        {
            var keys=aliases.Select(AliasKey).ToHashSet();
            if(concepts.Any(c=>c.State=="active"&&!except.Contains(c.Id)&&SameScopes(c.ScopeIds,scopes)&&c.Aliases.Any(a=>keys.Contains(AliasKey(a)))))
                throw new DomainException(409,"SKILL_ALIAS_CONFLICT");
        }
        if(review.Action=="new")
        {
            if(concepts.Count>=MaxConcepts)throw new DomainException(422,"SKILL_DICTIONARY_LIMIT");
            Text(review.Name,100);Text(review.Definition,500);Text(review.Boundary,500);Unique([alias]);
            var aliases=new[]{alias,alias with {Text=review.Name!}}.DistinctBy(AliasKey).ToArray();Unique(aliases);
            concepts.Add(new(newId,review.Name!,review.Definition!,review.Boundary!,scopes,"active",null,next,aliases));
        }
        else
        {
            var target=Target(review.TargetId);
            if(target.Aliases.Any(a=>a.Kind!=element.Kind))throw new DomainException(409,"SKILL_KIND_MISMATCH");
            if(review.Action=="alias"&&target.Aliases.Any(a=>AliasKey(a)==AliasKey(alias)))return before;
            IEnumerable<SkillAlias> additions=[alias];
            if(review.Action=="merge")
            {
                var source=Target(review.SourceId);
                if(source.Id==target.Id || Match(before,element,scopes).ConceptId!=source.Id)throw new DomainException(409,"SKILL_MERGE_SOURCE_MISMATCH");
                additions=source.Aliases;
                concepts[concepts.IndexOf(source)]=source with {State="merged",RedirectId=target.Id};
            }
            Unique(additions,target.Id,review.SourceId??"");
            var aliases=target.Aliases.Concat(additions).DistinctBy(AliasKey).ToArray();
            if(aliases.Length>40)throw new DomainException(422,"SKILL_ALIAS_LIMIT");
            concepts[concepts.IndexOf(target)]=target with {Aliases=aliases};
        }
        return new(next,concepts.ToArray());
    }
}

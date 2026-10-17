using System.Text.Json.Serialization;

namespace Dse.Domain;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DepartmentTerm([property: JsonRequired] string TermId, [property: JsonRequired] string? DepartmentId, [property: JsonRequired] DateTimeOffset From, [property: JsonRequired] DateTimeOffset? To);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DepartmentChange([property: JsonRequired] string Id, [property: JsonRequired] string Name, [property: JsonRequired] DateTimeOffset ValidFrom, [property: JsonRequired] DateTimeOffset? ValidTo,
    [property: JsonRequired] string[] SuccessorIds, [property: JsonRequired] string Reason, [property: JsonRequired] string ExpectedAuthorityToken);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MembershipChange([property: JsonRequired] string PersonId, [property: JsonRequired] DepartmentTerm[] Terms, [property: JsonRequired] string Reason, [property: JsonRequired] string ExpectedAuthorityToken);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonAccessChange([property: JsonRequired] string PersonId, [property: JsonRequired] string PrincipalId, [property: JsonRequired] string Oid, [property: JsonRequired] bool Enabled,
    [property: JsonRequired] string[] Roles, [property: JsonRequired] string[] ScopeIds, [property: JsonRequired] string Reason, [property: JsonRequired] string ExpectedAuthorityToken);

public static class OrganizationRules
{
    public static T Parse<T>(System.Text.Json.Nodes.JsonObject input)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(input.ToJsonString(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? throw new ArgumentException("Invalid organization input"); }
        catch (System.Text.Json.JsonException e) { throw new ArgumentException("Invalid organization input", e); }
    }
    public static void Terms(DepartmentTerm[] terms, DateTimeOffset now)
    {
        if (terms is null || terms.Length > 100 || terms.Any(t => t is null || !RecordJson.IsId(t.TermId) ||
            t.DepartmentId is not null && !RecordJson.IsId(t.DepartmentId) || t.From.Offset != TimeSpan.Zero ||
            t.To is not null && (t.To.Value.Offset != TimeSpan.Zero || t.To <= t.From) || t.From > now || t.To > now) ||
            terms.Select(t => t.TermId).Distinct().Count() != terms.Length) throw new ArgumentException("Invalid department terms");
        var ordered = terms.OrderBy(t => t.From).ToArray();
        for (var i = 1; i < ordered.Length; i++)
            if (ordered[i - 1].To is null || ordered[i].From < ordered[i - 1].To) throw new ArgumentException("Overlapping department terms");
    }
}

using System.Text.Json.Nodes;

namespace Dse.Domain;

public sealed record ActorAccess(string TenantId, string PrincipalId, string PersonId, string[] Roles, string[] ScopeIds)
{
    public string? AuthorityToken { get; init; }
    public string? AccessEpoch { get; init; }
    public bool HasRole(string role) => Roles.Contains(role, StringComparer.Ordinal);
    public SearchAccess Search => new(TenantId, ScopeIds);
}

public static class AccessPolicy
{
    public static bool InScope(ActorAccess actor, JsonObject value) => value.Text("tenantId") == actor.TenantId &&
        value.Strings("scopeIds").Intersect(actor.ScopeIds, StringComparer.Ordinal).Any();
    public static bool CanRead(ActorAccess actor, JsonObject value)
    {
        if (!InScope(actor, value)) return false;
        if (value["syntheticTest"]?.GetValue<bool>() == true)
            return actor.HasRole("member") && actor.HasRole("operator") && value.Text("ownerId") == actor.PrincipalId &&
                value.Text("managedByPersonId") == actor.PersonId && value.Strings("scopeIds").All(actor.ScopeIds.Contains);
        var owner = value.Text("ownerId") == actor.PrincipalId;
        return value.Text("kind") switch
        {
            "person" => actor.HasRole("member") && (value.Text("publicationState") != "withdrawn" || owner || actor.HasRole("operator")),
            "document" or "documentVersion" or "evidenceSpan" or "departmentActivity" => actor.HasRole("member"),
            "experience" => actor.HasRole("member") && value.Text("personId") == actor.PersonId,
            "session" => value.Strings("participantIds").Contains(actor.PersonId),
            "knowledge" => value.Text("status") == "published" ? actor.HasRole("member") :
                owner || value.Strings("participantIds").Contains(actor.PersonId) || (value.Text("status") == "review_requested" && actor.HasRole("publisher")),
            "job" => owner || actor.HasRole("operator"),
            "opportunity" => owner && actor.HasRole("analyst"),
            "retrievalRun" or "challenge" => owner,
            "participation" => owner || value.Text("personId") == actor.PersonId,
            // Message/Outcome/Feedback require their Session's current participants, never a copied ACL.
            _ => false
        };
    }
    public static bool CanUse(ActorAccess actor, JsonObject value) => actor.HasRole("member") && CanRead(actor, value) &&
        value["activeVersion"] is not null && value.Number("accessVersion") >= 1 && (value.Text("kind") switch
        {
            "person" => value["available"]?.GetValue<bool>() == true && value.Text("publicationState") != "withdrawn",
            "knowledge" => value.Text("status") == "published" && value["approvedBy"] is not null,
            "document" => true, // a newer queued version may coexist with the previous active version
            "departmentActivity" => true,
            _ => false
        });
    public static void RequireRead(ActorAccess actor, JsonObject value)
    {
        if (!CanRead(actor, value)) throw new DomainException(404, "NOT_FOUND");
    }
    public static void RequireSelf(ActorAccess actor, JsonObject person)
    {
        RequireRead(actor, person);
        if (person.Text("kind") != "person" || person.Text("id") != actor.PersonId || person.Text("ownerId") != actor.PrincipalId)
            throw new DomainException(403, "FORBIDDEN");
    }
}

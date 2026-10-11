using System.Security.Claims;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

public sealed record IdentityBinding(string TenantId, string Oid, string PrincipalId, string PersonId, string[] Roles, bool Enabled);
public sealed record AccessGrant(string PrincipalId, string[] ScopeIds);

public sealed class IdentityResolver
{
    internal IConfiguration Configuration { get; }
    private readonly IdentityBinding[] bindings;
    private readonly AccessGrant[] grants;
    public IdentityResolver(IConfiguration config)
    {
        Configuration = config;
        bindings = config.GetSection("Auth:Bindings").Get<IdentityBinding[]>() ?? [];
        grants = config.GetSection("Auth:Grants").Get<AccessGrant[]>() ?? [];
        var active = bindings.Where(x => x.Enabled).ToArray();
        if (active.Any(x => !Guid.TryParse(x.TenantId, out _) || !Guid.TryParse(x.Oid, out _) ||
                !RecordJson.IsId(x.PrincipalId) || !RecordJson.IsId(x.PersonId) || x.Roles is null || x.Roles.Length == 0 ||
                x.Roles.Distinct().Count() != x.Roles.Length || x.Roles.Any(r => r is not ("member" or "operator" or "publisher" or "analyst"))) ||
            active.GroupBy(x => (x.TenantId, x.Oid)).Any(g => g.Count() != 1) ||
            active.GroupBy(x => (x.TenantId, x.PersonId)).Any(g => g.Count() != 1) ||
            active.GroupBy(x => (x.TenantId, x.PrincipalId)).Any(g => g.Count() != 1) ||
            grants.GroupBy(x => x.PrincipalId).Any(g => g.Count() != 1) ||
            grants.Any(g => g.ScopeIds is null || g.ScopeIds.Length is < 1 or > 20 || g.ScopeIds.Any(s => !RecordJson.IsId(s)) || g.ScopeIds.Distinct().Count() != g.ScopeIds.Length))
            throw new InvalidOperationException("Invalid identity bindings or access grants");
    }
    public ActorAccess Resolve(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) throw new DomainException(401, "UNAUTHENTICATED");
        var binding = bindings.SingleOrDefault(b => b.Enabled && b.TenantId == user.FindFirstValue("tid") && b.Oid == user.FindFirstValue("oid"));
        var grant = grants.SingleOrDefault(g => g.PrincipalId == binding?.PrincipalId);
        if (binding is null || grant is null) throw new DomainException(403, "FORBIDDEN");
        return new(binding.TenantId, binding.PrincipalId, binding.PersonId, binding.Roles, grant.ScopeIds);
    }
    public ActorAccess? ResolvePerson(string tenant, string person)
    {
        var b = bindings.SingleOrDefault(b => b.Enabled && b.TenantId == tenant && b.PersonId == person);
        var g = grants.SingleOrDefault(g => g.PrincipalId == b?.PrincipalId);
        return b is null || g is null ? null : new(tenant, b.PrincipalId, b.PersonId, b.Roles, g.ScopeIds);
    }
    public static ActorAccess? DemoPerson(string person) => person is "demo-person-01" or "demo-person-02" or "demo-person-03"
        ? new("demo", "principal-demo-" + person[^2..], person, person == "demo-person-01" ? ["member", "publisher", "analyst"] : ["member"], ["demo"]) : null;
    public static ActorAccess Demo => new("demo", "principal-demo-01", "demo-person-01", ["member", "publisher", "analyst"], ["demo"]);
}

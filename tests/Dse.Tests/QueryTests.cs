using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public class QueryTests
{
    [Fact]
    public void Query_parameterizes_values_and_correlates_category_probability()
    {
        var request = new SearchRequest([new("domain", "x' OR 1=1")], ["handover"], [" ＡＢＣ "]);
        var query = CosmosCatalog.BuildQuery(request, new SearchAccess("demo", ["demo"]));
        Assert.DoesNotContain("x' OR 1=1", query.QueryText);
        Assert.Contains("c.categoryId = @category0 AND c.probability >= @probability", query.QueryText);
        Assert.Contains("IS_NUMBER(s.yesProbability)", query.QueryText);
        Assert.Contains("u.tenantId = @tenant", query.QueryText);
        Assert.Equal("abc", Assert.IsType<string[]>(query.GetQueryParameters().Single(x => x.Name == "@terms").Value)[0]);
    }
    [Fact]
    public void Knowledge_browse_has_a_safe_conditionless_query_and_keeps_tenant_scope_boundary()
    {
        var tenant = "tenant' OR 1=1"; var scope = "scope' OR 1=1";
        var query = CosmosCatalog.BuildKnowledgeBrowseQuery(new SearchAccess(tenant, [scope]));
        Assert.DoesNotContain("()", query.QueryText);
        Assert.DoesNotContain(tenant, query.QueryText); Assert.DoesNotContain(scope, query.QueryText);
        Assert.Contains("u.entityType = 'knowledge'", query.QueryText);
        Assert.Contains("u.status = 'active'", query.QueryText);
        Assert.Contains("u.available = true", query.QueryText);
        Assert.Contains("u.tenantId = @tenant", query.QueryText);
        Assert.Contains("ARRAY_CONTAINS(@scopes, s)", query.QueryText);
        Assert.Equal(tenant, query.GetQueryParameters().Single(x => x.Name == "@tenant").Value);
        Assert.Equal([scope], Assert.IsType<string[]>(query.GetQueryParameters().Single(x => x.Name == "@scopes").Value));
    }
}

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
}

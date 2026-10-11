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
        var searched = CosmosCatalog.BuildKnowledgeBrowseQuery(new SearchAccess(tenant,[scope]),[" ＡＢＣ ","x' OR 1=1"]);
        Assert.Contains("u.entityType = 'knowledge'",searched.QueryText);
        Assert.Contains("ARRAY_CONTAINS(@terms, t)",searched.QueryText);
        Assert.DoesNotContain("x' OR 1=1",searched.QueryText);
        Assert.Equal(["abc","x' or 1=1"],Assert.IsType<string[]>(searched.GetQueryParameters().Single(x => x.Name == "@terms").Value));
    }
    [Fact]
    public async Task Reservation_query_selects_only_the_exact_run_in_the_requested_tenant()
    {
        var store = new MemoryRecordStore();
        foreach (var (tenant, id, run) in new[] { ("demo", "reservation-target", "run-target"), ("demo", "reservation-other", "run-other"), ("other", "reservation-foreign", "run-target") })
        {
            var row = RecordJson.New("aiReservation", id, tenant, "operator", ["demo"], DateTimeOffset.UnixEpoch);
            row["runId"] = run;
            await store.CommitAsync(RecordPartition.Catalog(tenant), [RecordWrite.Create(row)], default);
        }
        var page = await store.QueryAsync(RecordPartition.Catalog("demo"), "aiReservation", "runId", "run-target", 100, null, default);
        Assert.Equal("reservation-target", Assert.Single(page.Items).Value.Text("id"));
        var injectedValue = await store.QueryAsync(RecordPartition.Catalog("demo"), "aiReservation", "runId", "run-target' OR 1=1", 100, null, default);
        Assert.Empty(injectedValue.Items);
    }
    [Theory]
    [InlineData("person", "runId")]
    [InlineData("aiReservation", "runId OR 1=1")]
    public async Task Reservation_query_does_not_expand_field_access_for_other_kinds_or_raw_fields(string kind, string field)
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await new MemoryRecordStore().QueryAsync(RecordPartition.Catalog("demo"), kind, field, "run-target", 100, null, default));
    }
}

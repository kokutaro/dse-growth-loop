using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public class SharedCommitTests
{
    private sealed class GroundedRanker : IReranker
    {
        public Task<AiResult<Ranking>> RerankAsync(AiContext context, string query, CancellationToken ct) =>
            Task.FromResult(new AiResult<Ranking>(AiStatus.Succeeded,
                new(context.Candidates.Select(c => new RankedCandidate(c.Id, c.SourceRefs[0].Text, [c.SourceRefs[0].Id])).ToArray()),
                "fake-search-to-share", null));
    }
    private const string Tenant = "11111111-1111-4111-8111-111111111111";
    [Theory]
    [InlineData(false, false, false, null)]
    [InlineData(true, false, false, null)]
    [InlineData(false, true, false, null)]
    [InlineData(false, false, true, null)]
    [InlineData(false, false, false, "evidence-version")]
    [InlineData(false, false, false, "evidence-scope")]
    public async Task Shared_commit_is_atomic_deduplicated_and_checks_all_participants(bool missingScope, bool revoke, bool changeSession, string? evidenceChange)
    {
        var store = new MemoryRecordStore(); await DemoRecords.SeedAsync(store, Tenant, default);
        var repo = new PrimaryRepository(store); var fence = new AccessFence(repo); var guard = new SourceGuard(repo);
        var settings = new Dictionary<string, string?>();
        for (var i = 0; i < 2; i++)
        {
            settings[$"Auth:Bindings:{i}:TenantId"] = Tenant;
            settings[$"Auth:Bindings:{i}:Oid"] = $"33333333-3333-4333-8333-33333333333{i}";
            settings[$"Auth:Bindings:{i}:PrincipalId"] = $"principal-{i}";
            settings[$"Auth:Bindings:{i}:PersonId"] = $"person-{i}";
            settings[$"Auth:Bindings:{i}:Roles:0"] = "member";
            settings[$"Auth:Bindings:{i}:Enabled"] = "true";
            settings[$"Auth:Grants:{i}:PrincipalId"] = $"principal-{i}";
            settings[$"Auth:Grants:{i}:ScopeIds:0"] = i == 1 && missingScope ? "private" : "demo";
        }
        var identities = new IdentityResolver(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        var actor = identities.ResolvePerson(Tenant, "person-0")!;
        var service = new SourceUseService(repo, guard, fence, identities);
        var config = new ConfigurationBuilder().Build();
        var search = new NaturalSearchService(new MemoryProjectionRepository(store), repo, guard, fence, null!, new GroundedRanker(), config, TimeProvider.System);
        NaturalSearchResponse? found = null;
        await search.SearchAndConsumeAsync(new("引き継ぎ", new([], [], ["引き継ぎ"]), "knowledge"), actor,
            (response, _) => { found = response; return Task.CompletedTask; }, default);
        Assert.Equal("succeeded", found!.RerankingStatus);
        var unit = Assert.Single(found.Primary.Candidates).Units[0];
        Assert.Equal("knowledge", unit.EntityType);
        var source = await guard.RequireAsync(actor, unit, default);
        var session = PersistenceTests.Example("Session"); session["tenantId"] = Tenant;
        session["participantIds"] = RecordJson.Array(["person-0", "person-1"]);
        var partition = RecordPartition.Conversation(Tenant, session.Text("id"));
        await repo.CommitAsync(partition, [RecordWrite.Create(session)], default);
        var saved = (await repo.ReadAsync(partition, session.Text("id"), default))!;
        if (revoke)
        {
            var original = (await repo.ReadAsync(RecordPartition.Catalog(Tenant), source.Unit.EntityId, default))!;
            original.Value["status"] = "withdrawn";
            await repo.CommitAsync(RecordPartition.Catalog(Tenant), [RecordWrite.Replace(original.Value, original.ETag)], default);
        }
        if (changeSession)
        {
            session["aiAssistance"] = "stopped";
            await repo.CommitAsync(partition, [RecordWrite.Replace(session, saved.ETag)], default);
        }
        if (evidenceChange is not null)
        {
            var original = (await repo.ReadAsync(RecordPartition.Catalog(Tenant), source.Unit.EntityId + "-document", default))!;
            if (evidenceChange == "evidence-version") original.Value["activeVersion"] = 2;
            else original.Value["scopeIds"] = RecordJson.Array(["private"]);
            await repo.CommitAsync(RecordPartition.Catalog(Tenant), [RecordWrite.Replace(original.Value, original.ETag)], default);
        }
        var message = PersistenceTests.Example("Message"); message["tenantId"] = Tenant;
        if (missingScope || revoke || changeSession || evidenceChange is not null)
        {
            await Assert.ThrowsAsync<DomainException>(() => service.CommitSharedAsync(actor, session.Text("id"), saved.ETag, source.Snapshots, message, default));
            Assert.Null(await repo.ReadAsync(partition, message.Text("id"), default));
        }
        else
        {
            await service.CommitSharedAsync(actor, session.Text("id"), saved.ETag, source.Snapshots, message, default);
            Assert.NotNull(await repo.ReadAsync(partition, message.Text("id"), default));
            var after = (await repo.ReadAsync(partition, session.Text("id"), default))!;
            message["id"] = "second-message";
            await Assert.ThrowsAsync<StoreConflictException>(() => service.CommitSharedAsync(actor, session.Text("id"), after.ETag, source.Snapshots, message, default));
            Assert.Null(await repo.ReadAsync(partition, "second-message", default));
            Assert.Equal(after.ETag, (await repo.ReadAsync(partition, session.Text("id"), default))!.ETag);
        }
    }
}

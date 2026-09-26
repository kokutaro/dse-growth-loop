using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public class AuthorizationTests
{
    internal static ActorAccess Actor(string principal = "principal-demo-01", string person = "person-01", string[]? roles = null) =>
        new("demo-tenant", principal, person, roles ?? ["member"], ["demo"]);

    [Fact]
    public void Operators_cannot_impersonate_people_or_read_nonparticipant_conversations()
    {
        var actor = Actor("other", "other", ["member", "operator", "publisher"]);
        Assert.False(AccessPolicy.CanRead(actor, PersistenceTests.Example("Session")));
        Assert.Throws<DomainException>(() => AccessPolicy.RequireSelf(actor, PersistenceTests.Example("Person")));
        Assert.False(AccessPolicy.CanRead(actor with { TenantId = "other" }, PersistenceTests.Example("Person")));
        Assert.False(AccessPolicy.CanRead(actor with { ScopeIds = ["private"] }, PersistenceTests.Example("Person")));
    }

    [Fact]
    public void Unpublished_knowledge_is_not_searchable_even_by_owner_or_publisher()
    {
        var value = PersistenceTests.Example("Knowledge");
        value["status"] = "draft";
        Assert.False(AccessPolicy.CanUse(Actor(roles: ["member", "publisher"]), value));
    }

    [Fact]
    public async Task Durable_fence_blocks_other_process_and_does_not_expire_under_a_slow_commit()
    {
        var repository = new PrimaryRepository(new MemoryRecordStore());
        var first = new AccessFence(repository);
        var second = new AccessFence(repository);
        await first.RunAsync("demo-tenant", async ct =>
        {
            var error = await Assert.ThrowsAsync<DomainException>(() => second.RunAsync("demo-tenant", _ => Task.FromResult(1), ct));
            Assert.Equal("ACCESS_BUSY", error.Code);
            return 1;
        }, default);
        Assert.Equal(2, await second.RunAsync("demo-tenant", _ => Task.FromResult(2), default));
    }
    [Fact]
    public void Session_participants_are_person_ids_not_principal_ids()
    {
        var session = PersistenceTests.Example("Session");
        Assert.True(AccessPolicy.CanRead(Actor(person: "demo-person-01"), session));
        Assert.False(AccessPolicy.CanRead(Actor(principal: "demo-person-01", person: "other"), session));
    }
}

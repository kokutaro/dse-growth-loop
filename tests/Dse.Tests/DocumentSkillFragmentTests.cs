using System.Text.Json;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public sealed class DocumentSkillFragmentTests
{
    private sealed class KnowledgeSkills : IConcreteSkillExtractor
    {
        public Task<AiResult<SpecificSkills>> ExtractSkillsAsync(AiContext c,string text,CancellationToken ct)=>
            Task.FromResult(new AiResult<SpecificSkills>(AiStatus.Succeeded,new(SpecificSkillRules.Version,"extracted",
                [new("Git","Git",c.Evidence[0].Id,text){Basis="knowledge",ContentKind="technology_list"}]),"offline-knowledge",null));
    }
    [Fact]
    public async Task Normal_pipeline_keeps_collectable_document_content_without_a_person_experience_result()
    {
        using var h=new IngestionHarness();const string text="技術一覧：Git";
        var accepted=await h.Upload(bytes:OfficeFixtures.Package(body:$"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>"));
        await h.Processor.RunOnceAsync(default);var p=RecordPartition.Catalog("demo");
        var doc=(await h.Repo.ReadAsync(p,accepted.Body!["documentId"]!.GetValue<string>(),default))!;
        var ai=new PipelineAi();var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Data:Mode"]="Demo",["Ai:Mode"]="Mock"}).Build();
        var pipeline=new ProfilePipeline(h.Repo,h.Store,new(h.Repo),ai,ai,new(config),config,h.Clock,new KnowledgeSkills());
        await pipeline.CommitAsync(await pipeline.BuildAsync(Jobs.Create(doc.Value,"enrichment",h.Clock.GetUtcNow()),default),[],default);
        var fragment=Assert.Single((await h.Repo.QueryAsync(p,"documentFragment","documentId",doc.Value.Text("id"),100,null,default)).Items);
        Assert.Equal(text,fragment.Value.Text("description"));Assert.Equal("content_only",fragment.Value["specificSkills"]!["items"]![0]!["analysis"]!["experienceState"]!.GetValue<string>());
        // Explicit test authority only; the production service must not turn a member into an operator.
        var authority=(await h.Repo.ReadAsync(p,AuthorityProvider.PersonId(IdentityResolver.Demo.PersonId),default))!;
        authority.Value["roles"]=RecordJson.Array([..IdentityResolver.Demo.Roles,"operator"]);await h.Repo.CommitAsync(p,[RecordWrite.Replace(authority.Value,authority.ETag)],default);
        var curator=IdentityResolver.Demo with{Roles=[..IdentityResolver.Demo.Roles,"operator"]};
        var dictionary=new SkillDictionaryService(h.Repo,new(h.Repo),new(h.Repo,h.Clock,h.Store),h.Clock);
        var collected=await dictionary.CollectAsync(curator,"normal-document-skills",new(fragment.Value.Text("id")),default);
        Assert.Equal(1,collected.Body!["created"]!.GetValue<int>());
        var units=(await h.Store.QueryAsync(p,"searchUnit",null,null,100,null,default)).Items.Select(r=>r.Value.Deserialize<SearchUnit>(ProjectionRules.Json)!).ToArray();
        var current=await new SourceGuard(h.Repo).FilterAsync(IdentityResolver.Demo,units,default);
        Assert.DoesNotContain(current,u=>u.EntityId==IdentityResolver.Demo.PersonId);
        doc=(await h.Repo.ReadAsync(p,doc.Value.Text("id"),default))!;doc.Value["activeVersion"]=null;
        await h.Repo.CommitAsync(p,[RecordWrite.Replace(doc.Value,doc.ETag)],default);
        Assert.Equal(404,(await Assert.ThrowsAsync<DomainException>(()=>dictionary.CollectAsync(curator,"withdrawn-document-skills",new(fragment.Value.Text("id")),default))).Status);
    }
}

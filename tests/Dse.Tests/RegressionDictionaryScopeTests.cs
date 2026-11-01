using Dse.Domain;
using Dse.Evaluation;
using Xunit;

namespace Dse.Tests;

public sealed class RegressionDictionaryScopeTests
{
    [Fact]
    public void Approved_development_scope_versions_only_the_selected_concept_without_mutating_history()
    {
        var concept=new SkillConcept("migration","データ移行","旧定義","旧境界",["isolated"],"active",null,1,[new("移行","practice",null,[])]);
        var other=concept with{Id="other",Name="別技能"};var prior=new SkillDictionarySnapshot(1,[concept,other]);
        var next=RegressionDictionaryScope.IncludeMigrationDevelopment(prior,concept.Id);
        Assert.Equal(1,prior.Version);Assert.Equal("旧定義",prior.Concepts[0].Definition);Assert.Equal("旧境界",prior.Concepts[0].Boundary);
        Assert.Equal(2,next.Version);var changed=next.Concepts.Single(x=>x.Id==concept.Id);Assert.Equal(concept.IntroducedVersion,changed.IntroducedVersion);
        Assert.Equal(concept.Aliases,changed.Aliases);Assert.Equal(concept.ScopeIds,changed.ScopeIds);Assert.Equal(concept.State,changed.State);
        Assert.Contains("開発",changed.Definition);Assert.Contains("実施したことは必須としない",changed.Definition);
        Assert.Contains("移行実行経験を認定しない",changed.Boundary);Assert.Same(other,next.Concepts[1]);
    }

    [Fact]
    public void Missing_or_ambiguous_target_cannot_silently_modify_an_experimental_dictionary()
    {
        var c=new SkillConcept("id","技能","定義","境界",["isolated"],"active",null,1,[]);
        Assert.Throws<ArgumentException>(()=>RegressionDictionaryScope.IncludeMigrationDevelopment(new(1,[c]),"unknown"));
        Assert.Throws<ArgumentException>(()=>RegressionDictionaryScope.IncludeMigrationDevelopment(new(1,[c,c]),c.Id));
    }
}

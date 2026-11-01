using System.Reflection;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public sealed class ProcessingBuildIdentityTests
{
    [Theory]
    [InlineData(null)][InlineData("")][InlineData("1.0.0")][InlineData("arbitrary-retry-version")][InlineData("abc")]
    public void Missing_or_invalid_metadata_cannot_start_a_generation(string? hash)
    {
        var items=hash is null?Array.Empty<AssemblyMetadataAttribute>():[new("Dse.ProcessingSourceSha256",hash)];
        Assert.Equal("BUILD_IDENTITY_UNAVAILABLE",Assert.Throws<DomainException>(()=>ProcessingBuildIdentity.Read(items)).Code);
    }
    [Fact]
    public void Duplicate_metadata_is_rejected_and_valid_content_hash_is_normalized()
    {
        var attr=new AssemblyMetadataAttribute("Dse.ProcessingSourceSha256",new string('A',64));
        Assert.Equal("source-sha256:"+new string('a',64),ProcessingBuildIdentity.Read([attr]));
        Assert.Throws<DomainException>(()=>ProcessingBuildIdentity.Read([attr,attr]));
    }
}

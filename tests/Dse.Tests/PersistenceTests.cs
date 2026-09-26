using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public class PersistenceTests
{
    internal static JsonObject Example(string kind)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DseGrowthLoop.slnx"))) root = root.Parent;
        var examples = JsonNode.Parse(File.ReadAllText(Path.Combine(root!.FullName, "docs/specs/examples.json")))!.AsArray();
        return examples.First(x => x!["schema"]!.GetValue<string>() == kind)!["value"]!.DeepClone().AsObject();
    }

    [Fact]
    public async Task Batch_conflict_leaves_no_partial_document_or_job()
    {
        var store = new MemoryRecordStore();
        var partition = RecordPartition.Catalog("demo-tenant");
        var document = Example("Document");
        await store.CommitAsync(partition, [RecordWrite.Create(document)], default);
        var job = Example("Job");
        await Assert.ThrowsAsync<StoreConflictException>(() => store.CommitAsync(partition,
            [RecordWrite.Create(job), RecordWrite.Replace(document, "\"wrong\"")], default));
        Assert.Null(await store.ReadAsync(partition, job.Text("id"), default));
    }

    [Fact]
    public async Task Partition_and_container_mismatch_are_rejected_before_saving()
    {
        var store = new MemoryRecordStore();
        await Assert.ThrowsAsync<ArgumentException>(() => store.CommitAsync(RecordPartition.Catalog("other"),
            [RecordWrite.Create(Example("Document"))], default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CommitAsync(RecordPartition.Catalog("demo-tenant"),
            [RecordWrite.Create(Example("Session"))], default));
    }

    [Fact]
    public async Task Every_contract_kind_roundtrips_without_losing_type_specific_fields()
    {
        var store = new MemoryRecordStore();
        foreach (var kind in RecordJson.ContractKinds.Values)
        {
            var value = Example(kind);
            var partition = RecordPartition.For(value);
            await store.CommitAsync(partition, [RecordWrite.Create(value)], default);
            var saved = (await store.ReadAsync(partition, value.Text("id"), default))!;
            value["etag"] = saved.ETag;
            Assert.True(JsonNode.DeepEquals(value, saved.Value));
        }
    }

    [Fact]
    public async Task Primary_repository_rejects_search_projections()
    {
        var repository = new PrimaryRepository(new MemoryRecordStore());
        await Assert.ThrowsAsync<ArgumentException>(() => repository.CommitAsync(RecordPartition.Catalog("demo-tenant"),
            [RecordWrite.Create(Example("SearchUnit"))], default));
    }
}

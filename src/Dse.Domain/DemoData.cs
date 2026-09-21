namespace Dse.Domain;

public static class DemoData
{
    public static SearchUnit[] Create(string tenantId) =>
    [
        Unit("demo-person-01", "person", "青葉 はるか", "営業企画部", "営業案件の引き継ぎ項目を整理し、確認漏れを減らした。", tenantId,
            [new("domain", "sales", 0.7), new("domain", "operations", 0.3)], ["引き継ぎ", "営業", "標準化"], 0.9),
        Unit("demo-person-02", "person", "水野 そうた", "製造技術部", "製造ラインの交代時チェックリストを整備した。", tenantId,
            [new("domain", "manufacturing", 0.7), new("domain", "operations", 0.3)], ["引き継ぎ", "チェックリスト", "標準化"], 0.85),
        Unit("demo-person-03", "person", "森 あかり", "情報システム部", "機械設計4年の後、業務ソフトウェア開発に3年間従事した。", tenantId,
            [new("domain", "mechanical", 0.7), new("domain", "software", 0.3)], ["ソフトウェア", "開発"], null),
        Unit("demo-knowledge-01", "knowledge", "担当変更時の確認項目を標準化", "架空の公開事例", "担当変更時の確認リストと引き継ぎ会議を導入。窓口と未完了事項を明記した。", tenantId,
            [new("domain", "operations", 0.8), new("domain", "sales", 0.2)], ["引き継ぎ", "チェックリスト", "標準化"], 0.95)
    ];

    private static SearchUnit Unit(string id, string type, string title, string department, string text,
        string tenant, CategoryProbability[] categories, string[] terms, double? signal) =>
        new(id, id, type, tenant, id, title, department + "：" + text, ["demo"], "active", true,
            Retriever.TaxonomyVersion, categories, [new("handover", signal, signal is null ? "unknown" : "supported")],
            terms, [new(id + "-source", "ダミー資料・" + department, "段落1", text)], "fixture");
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

/// <summary>本文や認証情報を含めず、fence取得元の認証済み操作を関連付けます。</summary>
public static class FenceOwnerTrace
{
    private static readonly AsyncLocal<JsonObject?> Ambient=new();
    /// <summary>サーバー定義のルート型と現在の認可主体から、操作中だけ有効な診断文脈を作ります。</summary>
    public static IDisposable BeginApi(string routeTemplate,ActorAccess actor)
    {
        var previous=Ambient.Value;
        var route=routeTemplate.Length<=180&&routeTemplate.All(c=>char.IsAsciiLetterOrDigit(c)||"/_{}:.- ".Contains(c))?routeTemplate:"api";
        Ambient.Value=new(){["origin"]="api",["operation"]=route,["correlationId"]=Guid.NewGuid().ToString("N"),
            ["actorHash"]=Hash(actor.TenantId+":"+actor.PrincipalId),
            ["scopeHash"]=Hash(System.Text.Json.JsonSerializer.Serialize(actor.ScopeIds.Order(StringComparer.Ordinal)))};
        return new Restore(()=>Ambient.Value=previous);
    }
    internal static JsonObject Capture(TimeProvider clock)
    {
        var result=Ambient.Value?.DeepClone().AsObject()??new JsonObject{["origin"]="internal",["correlationId"]=Guid.NewGuid().ToString("N")};
        if(PipelineDiagnostics.OwnerContext() is{} job)
            foreach(var (name,value) in job)result[name]=value?.DeepClone();
        result["version"]="fence-owner-v1";result["state"]="acquired";result["acquiredAt"]=clock.GetUtcNow();
        return result;
    }
    internal static string Hash(string text)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private sealed class Restore(Action restore):IDisposable{private bool ended;public void Dispose(){if(!ended){ended=true;restore();}}}
}

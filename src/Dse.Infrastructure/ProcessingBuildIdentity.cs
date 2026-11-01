using System.Reflection;
using System.Text.RegularExpressions;
using Dse.Domain;

namespace Dse.Infrastructure;

/// <summary>共通処理の実ソースとビルド条件から生成した識別子。任意の運用設定では上書きしない。</summary>
public static class ProcessingBuildIdentity
{
    /// <summary>メタデータ欠落・重複・不正値は再抽出を開始せず安全側で拒否する。</summary>
    public static string Read(IEnumerable<AssemblyMetadataAttribute> metadata)
    {
        var values=metadata.Where(x=>x.Key=="Dse.ProcessingSourceSha256").Select(x=>x.Value).ToArray();
        if(values.Length!=1||values[0] is not string hash||!Regex.IsMatch(hash,"\\A[0-9a-fA-F]{64}\\z",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))
            throw new DomainException(409,"BUILD_IDENTITY_UNAVAILABLE");
        return "source-sha256:"+hash.ToLowerInvariant();
    }
    /// <summary>APIとWorkerが参照する同一Infrastructureアセンブリの識別子。</summary>
    public static string Current=>Read(typeof(ProfilePipeline).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>());
}

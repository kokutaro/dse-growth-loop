using Dse.Infrastructure;

var seed = args.Contains("--seed-demo");
var builder = Host.CreateApplicationBuilder(args.Where(x => x != "--seed-demo").ToArray());
builder.Services.AddCatalog(builder.Configuration, builder.Environment.IsDevelopment());
using var host = builder.Build();
if (seed)
{
    var tenant = builder.Configuration["Auth:TenantId"];
    if (string.IsNullOrWhiteSpace(tenant)) throw new InvalidOperationException("Auth__TenantId が必要です。");
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
    await host.Services.GetRequiredService<ICatalog>().SeedDemoAsync(tenant, timeout.Token);
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Seed").LogInformation("ダミーデータ投入完了。既存レコードは維持しました。");
    return;
}
host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Worker").LogInformation(
    "Worker 基盤を起動しました。文書取込ハンドラーは未実装のため Queue メッセージは消費しません。");
await host.RunAsync();

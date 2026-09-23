using Dse.Infrastructure;

if (args.Length != 1 || args[0] != "--seed-demo")
    throw new ArgumentException("現在のWorkerは --seed-demo の手動実行専用です。取込処理は未実装です。");
var builder = Host.CreateApplicationBuilder(args.Where(x => x != "--seed-demo").ToArray());
builder.Services.AddCatalog(builder.Configuration, builder.Environment.IsDevelopment());
using var host = builder.Build();
var tenant = builder.Configuration["Auth:TenantId"];
if (string.IsNullOrWhiteSpace(tenant)) throw new InvalidOperationException("Auth__TenantId が必要です。");
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
await host.Services.GetRequiredService<ICatalog>().SeedDemoAsync(tenant, timeout.Token);
host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Seed").LogInformation("ダミーデータ投入完了。既存レコードは維持しました。");

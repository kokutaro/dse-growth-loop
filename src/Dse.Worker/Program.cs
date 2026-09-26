using Dse.Infrastructure;

if (args.Length != 1 || args[0] is not ("--seed-demo" or "--recover-fence"))
    throw new ArgumentException("--seed-demo または --recover-fence の手動実行専用です。取込処理は未実装です。");
var builder = Host.CreateApplicationBuilder([]);
builder.Services.AddCatalog(builder.Configuration, builder.Environment.IsDevelopment());
using var host = builder.Build();
var tenant = builder.Configuration["Auth:TenantId"];
if (string.IsNullOrWhiteSpace(tenant)) throw new InvalidOperationException("Auth__TenantId が必要です。");
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
if (args[0] == "--seed-demo")
{
    await host.Services.GetRequiredService<ICatalog>().SeedDemoAsync(tenant, timeout.Token);
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Seed").LogInformation("ダミーデータ投入完了。既存レコードは維持しました。");
}
else
{
    var etag = builder.Configuration["Recovery:FenceETag"];
    if (builder.Configuration["Recovery:ProcessesStopped"] != "true" || string.IsNullOrWhiteSpace(etag))
        throw new InvalidOperationException("API/Worker停止確認後に Recovery__ProcessesStopped=true と Recovery__FenceETag を設定してください。");
    var sessions = builder.Configuration.GetSection("Recovery:ConversationIds").Get<string[]>() ?? [];
    await new FenceRecovery(host.Services.GetRequiredService<IRecordStore>()).RecoverAsync(tenant, etag, sessions, timeout.Token);
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Recovery").LogInformation("冪等記録とアクセスフェンスの手動回復完了。");
}

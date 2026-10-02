using Dse.Infrastructure;
using Dse.Domain;

var command = args.Length == 0 ? "--consume" : args[0];
if (args.Length > 1 || command is not ("--consume" or "--once" or "--seed-demo" or "--recover-fence" or "--reconcile" or "--jobs" or "--cleanup-staging" or "--ai-smoke" or "--chat-smoke" or "--connection-smoke" or "--recover-ai-slot" or "--publish-knowledge"))
    throw new ArgumentException("--consume / --once / --seed-demo / --recover-fence / --reconcile / --jobs / --cleanup-staging / --ai-smoke / --connection-smoke / --recover-ai-slot / --publish-knowledge");
var builder = Host.CreateApplicationBuilder([]);
if (command == "--ai-smoke") AiSmoke.Validate(builder.Configuration);
if (command == "--chat-smoke") ChatSmoke.Validate(builder.Configuration);
if (command == "--connection-smoke")
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
    if (!await ConnectionSmoke.RunAsync(builder.Configuration, deadline.Token)) Environment.ExitCode = 1;
    return;
}
builder.Services.AddCatalog(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddIngestion(builder.Configuration);
if (command == "--consume") builder.Services.AddHostedService<IngestionBackgroundService>();
using var host = builder.Build();
if (command == "--consume") { await host.RunAsync(); return; }
var tenant = builder.Configuration["Auth:TenantId"] ?? (builder.Configuration["Data:Mode"] == "Demo" ? "demo" : null);
if (string.IsNullOrWhiteSpace(tenant) || !RecordJson.IsId(tenant)) throw new InvalidOperationException("Auth__TenantId が必要です。");
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(command == "--chat-smoke" ? 15 : 4));
var ct = timeout.Token;
switch (command)
{
    case "--chat-smoke":
        if (!await ChatSmoke.RunAsync(builder.Configuration, host.Services.GetRequiredService<IdentityResolver>(),
            host.Services.GetRequiredService<IPrimaryRepository>(), host.Services.GetRequiredService<ConversationService>(),
            host.Services.GetRequiredService<ChatAssistanceService>(), host.Services.GetRequiredService<IIngestionQueue>(),
            host.Services.GetRequiredService<IProjectionRepository>(), host.Services.GetRequiredService<IIngestionBlobs>(), ct)) Environment.ExitCode = 1;
        break;
    case "--publish-knowledge":
        var publicationSessions = builder.Configuration.GetSection("Recovery:ConversationIds").Get<string[]>() ?? [];
        if (publicationSessions.Length == 0) throw new ArgumentException("Recovery__ConversationIds required");
        foreach (var sid in publicationSessions)
            Console.WriteLine($"Recovered: {await host.Services.GetRequiredService<KnowledgeService>().RecoverAsync(tenant, sid, ct)}");
        break;
    case "--ai-smoke":
        if (!await AiSmoke.RunAsync(builder.Configuration, host.Services.GetRequiredService<IdentityResolver>(),
            host.Services.GetRequiredService<IPrimaryRepository>(), host.Services.GetRequiredService<IDecisionProvider>(),
            host.Services.GetRequiredService<ITextStructurer>(), host.Services.GetRequiredService<IReranker>(),
            host.Services.GetRequiredService<IProjectionRepository>(), ct)) Environment.ExitCode = 1;
        break;
    case "--recover-ai-slot":
        var reservationId = builder.Configuration["Recovery:AiReservationId"];
        if (string.IsNullOrWhiteSpace(reservationId)) throw new ArgumentException("Recovery__AiReservationId required");
        await host.Services.GetRequiredService<AiLedger>().RecoverSlotAsync(tenant, reservationId,
            builder.Configuration["Recovery:ProcessesStopped"] == "true", ct);
        break;
    case "--seed-demo":
        await host.Services.GetRequiredService<ICatalog>().SeedDemoAsync(tenant, ct); break;
    case "--once":
        await host.Services.GetRequiredService<IngestionProcessor>().RunOnceAsync(ct); break;
    case "--reconcile":
        Console.WriteLine($"Notified: {await host.Services.GetRequiredService<JobNotifications>().ReconcileAsync(tenant, ct)}"); break;
    case "--jobs":
        string? cursor = null;
        do
        {
            var page = await host.Services.GetRequiredService<IPrimaryRepository>().QueryAsync(RecordPartition.Catalog(tenant), "job", null, null, 100, cursor, ct);
            foreach (var row in page.Items.Where(x => x.Value.Text("jobType") is "ingestion" or "enrichment" or "projection"))
                Console.WriteLine($"{row.Value.Text("id")} {row.Value.Text("jobType")} {row.Value.Text("status")} attempt={row.Value.Number("attempt")} code={row.Value.Text("lastErrorCode")} notification={row.Value.Text("notificationState")}");
            cursor = page.ContinuationToken;
        } while (cursor is not null);
        break;
    case "--cleanup-staging":
        if (builder.Configuration["Cleanup:ProcessesStopped"] != "true") throw new InvalidOperationException("API/Worker停止確認に Cleanup__ProcessesStopped=true が必要です。");
        var cleanup = new StagingCleanup(host.Services.GetRequiredService<IPrimaryRepository>(), host.Services.GetRequiredService<IIngestionBlobs>(), TimeProvider.System);
        var apply = builder.Configuration["Cleanup:Apply"] == "true";
        var keys = builder.Configuration.GetSection("Cleanup:Keys").Get<string[]>() ?? [];
        foreach (var key in await cleanup.RunAsync(tenant, apply, keys, ct)) Console.WriteLine($"{(apply ? "deleted" : "candidate")}: {key}");
        break;
    case "--recover-fence":
        var etag = builder.Configuration["Recovery:FenceETag"];
        if (builder.Configuration["Recovery:ProcessesStopped"] != "true" || string.IsNullOrWhiteSpace(etag))
            throw new InvalidOperationException("API/Worker停止確認後に Recovery__ProcessesStopped=true と Recovery__FenceETag を設定してください。");
        var sessions = builder.Configuration.GetSection("Recovery:ConversationIds").Get<string[]>() ?? [];
        await new FenceRecovery(host.Services.GetRequiredService<IRecordStore>()).RecoverAsync(tenant, etag, sessions, ct); break;
}

using Microsoft.AspNetCore.RateLimiting;
using System.IO.Compression;
using System.Xml.Linq;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json.Serialization;
// Loopback-only in-memory browser test host. No production auth override or cloud transport.
var builder=WebApplication.CreateBuilder(args);builder.WebHost.UseUrls("http://127.0.0.1:5183");
builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{["Data:Mode"]="Demo",["Ai:Mode"]="Mock",["AiUsage:Stopped"]="false",["AiUsage:MaxOutputTokens"]="4096"});
builder.Services.AddCatalog(builder.Configuration,true);builder.Services.AddIngestion(builder.Configuration);
var memory=new MemoryRecordStore();builder.Services.AddSingleton<IRecordStore>(memory);
var manager=new ActorAccess("demo","browser-manager","browser-manager-person",["member","operator"],["demo"]);
var member=new ActorAccess("demo","browser-member","browser-member-person",["member"],["demo"]);
await AuthorityProvider.InitializeAsync(memory,"demo",new[]{manager,member}.Select(a=>new IdentityBinding("demo","",a.PrincipalId,a.PersonId,a.Roles,true)).ToArray(),new[]{manager,member}.Select(a=>new AccessGrant(a.PrincipalId,a.ScopeIds)).ToArray(),default);
var managerRecord=RecordJson.New("person",manager.PersonId,"demo",manager.PrincipalId,["demo"],DateTimeOffset.UtcNow);managerRecord["displayName"]="運用者fixture";managerRecord["departmentId"]="existing-department";await memory.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Create(managerRecord)],default);
builder.Services.AddRateLimiter(o=>{o.AddConcurrencyLimiter("upload",x=>{x.PermitLimit=10;x.QueueLimit=0;});o.AddConcurrencyLimiter("search",x=>{x.PermitLimit=10;x.QueueLimit=0;});});
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();builder.Services.AddSingleton<PageTokens>();
builder.Services.ConfigureHttpJsonOptions(o=>o.SerializerOptions.UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow);
var app=builder.Build();app.UseRateLimiter();
app.Use(async(c,next)=>{c.Items[typeof(ActorAccess)]=c.Request.Headers["X-Test-Actor"]=="member"?member:manager;
 try{await next();}catch(DomainException e){c.Response.StatusCode=e.Status;await c.Response.WriteAsJsonAsync(new{code=e.Code});}catch(ArgumentException){c.Response.StatusCode=400;await c.Response.WriteAsJsonAsync(new{code="INVALID_INPUT"});}});
app.MapGet("/api/config",()=>new{mode="Demo",tenantId="demo",clientId="",scope=""});
app.MapGet("/health/live",()=>Results.Ok("test-only"));
app.MapPost("/api/test/drain",async(IngestionProcessor processor)=>{for(var i=0;i<8;i++)if(!await processor.RunOnceAsync(default))break;return Results.Ok();});
app.MapPost("/api/test/revoke",async()=>{var p=RecordPartition.Catalog("demo");var row=(await memory.ReadAsync(p,AuthorityProvider.PersonId(manager.PersonId),default))!;row.Value["roles"]=RecordJson.Array(["member"]);await memory.CommitAsync(p,[RecordWrite.Replace(row.Value,row.ETag)],default);return Results.Ok();});
app.MapGet("/api/test/workbook",()=>{
 var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"DseGrowthLoop.slnx")))root=root.Parent;
 using var input=new MemoryStream(File.ReadAllBytes(Path.Combine(root!.FullName,"web/tests/fixtures/internal-resume.xlsx")));
 using var source=new ZipArchive(input,ZipArchiveMode.Read);using var output=new MemoryStream();
 using(var target=new ZipArchive(output,ZipArchiveMode.Create,true))foreach(var entry in source.Entries){var dest=target.CreateEntry(entry.FullName);using var read=entry.Open();using var write=dest.Open();if(entry.FullName=="xl/worksheets/sheet2.xml"){var xml=XDocument.Load(read);XNamespace n="http://schemas.openxmlformats.org/spreadsheetml/2006/main";foreach(var row in xml.Descendants(n+"row").Where(r=>(int?)r.Attribute("r")>=31).ToArray())row.Remove();xml.Save(write);}else read.CopyTo(write);}
 return Results.File(output.ToArray(),OfficeExtractor.ExcelMime,"two-project-synthetic.xlsx");
});
var api=app.MapGroup("/api/v1");api.MapSyntheticTests();api.MapRecords();api.MapDocuments();
app.Run();

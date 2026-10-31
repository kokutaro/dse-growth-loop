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
builder.Services.AddSingleton<IWorkbookStructurer, BrowserWorkbookSections>();
var app=builder.Build();app.UseRateLimiter();
app.Use(async(c,next)=>{c.Items[typeof(ActorAccess)]=c.Request.Headers["X-Test-Actor"]=="member"?member:manager;
 try{await next();}catch(DomainException e){c.Response.StatusCode=e.Status;await c.Response.WriteAsJsonAsync(new{code=e.Code});}catch(ArgumentException){c.Response.StatusCode=400;await c.Response.WriteAsJsonAsync(new{code="INVALID_INPUT"});}});
app.MapGet("/api/config",()=>new{mode="Demo",tenantId="demo",clientId="",scope=""});
app.MapGet("/health/live",()=>Results.Ok("test-only"));
app.MapPost("/api/test/drain",async(IngestionProcessor processor)=>{for(var i=0;i<8;i++)if(!await processor.RunOnceAsync(default))break;return Results.Ok();});
app.MapPost("/api/test/revoke",async()=>{var p=RecordPartition.Catalog("demo");var row=(await memory.ReadAsync(p,AuthorityProvider.PersonId(manager.PersonId),default))!;row.Value["roles"]=RecordJson.Array(["member"]);await memory.CommitAsync(p,[RecordWrite.Replace(row.Value,row.ETag)],default);return Results.Ok();});
app.MapGet("/api/test/workbook",()=>{
 const string n="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
 // Small, separately generated flow fixture; frozen larger books are covered by capacity tests.
 var cells=new Dictionary<string,string>{["A1"]="自己PR：構成設計は希望であり未実施。",["F23"]="合成案件1",["G24"]="C#で回帰試験を実施した。",["Q25"]="PostgreSQL 16",["F27"]="合成案件2",["G28"]="C#で回帰試験を実施した。",["Q29"]="PostgreSQL 16"};
 var sheet=new XElement(XName.Get("worksheet",n),new XElement(XName.Get("sheetData",n),cells.Select(p=>new XElement(XName.Get("row",n),new XElement(XName.Get("c",n),new XAttribute("r",p.Key),new XAttribute("t","inlineStr"),new XElement(XName.Get("is",n),new XElement(XName.Get("t",n),p.Value)))))));
 var parts=new Dictionary<string,string>{
 ["[Content_Types].xml"]="<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/></Types>",
 ["_rels/.rels"]="<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
 ["xl/workbook.xml"]=$"<workbook xmlns=\"{n}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"合成の任意シート\" sheetId=\"1\" r:id=\"s1\"/></sheets></workbook>",
 ["xl/_rels/workbook.xml.rels"]="<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"s1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>",
 ["xl/worksheets/sheet1.xml"]=sheet.ToString()};
 using var output=new MemoryStream();using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))foreach(var part in parts){using var writer=new StreamWriter(zip.CreateEntry(part.Key,CompressionLevel.NoCompression).Open());writer.Write(part.Value);}
 return Results.File(output.ToArray(),OfficeExtractor.ExcelMime,"two-project-synthetic.xlsx");
});
var api=app.MapGroup("/api/v1");api.MapSyntheticTests();api.MapRecords();api.MapDocuments();
app.Run();

sealed class BrowserWorkbookSections(IPrimaryRepository repo) : IWorkbookStructurer
{
 public async Task<AiResult<WorkbookStructure>> StructureWorkbookAsync(AiContext c,CancellationToken ct)
 {
  var groups=new Dictionary<string,List<string>>{["one"]=[],["two"]=[],["context"]=[]};
  foreach(var e in c.Evidence){var r=(await repo.ReadAsync(RecordPartition.Catalog(c.Actor.TenantId),e.Id,ct))!.Value;var cell=r["sourceCell"];
   var address=cell?["address"]?.GetValue<string>()??"";var row=int.TryParse(new string(address.SkipWhile(char.IsAsciiLetter).ToArray()),out var n)?n:0;
   var key=row is >=23 and <=26?"one":row is >=27 and <=30?"two":"context";groups[key].Add(e.Id);}
  return new(AiStatus.Succeeded,new(groups.Where(g=>g.Value.Count>0).Select(g=>new WorkbookSection(g.Key=="context"?"context":"project",g.Value.ToArray())).ToArray()),"browser-fixture-workbook",null);
 }
}

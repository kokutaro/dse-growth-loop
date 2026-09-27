var builder = DistributedApplication.CreateBuilder(args);
var api = builder.AddProject<Projects.Dse_Api>("api")
    .WithEnvironment("Data__Mode", "Demo")
    .WithEnvironment("Data__LocalPath", Path.GetFullPath("../../.artifacts/local-ingestion", builder.Environment.ContentRootPath))
    .WithEnvironment("Ingestion__RunWorker", "true");
builder.AddExecutable("web", "npm", "../../web", "run", "dev", "--", "--host", "127.0.0.1")
    .WithEnvironment("API_PROXY_TARGET", api.GetEndpoint("http"))
    .WithHttpEndpoint(port: 5173, targetPort: 5173, isProxied: false)
    .WaitFor(api);
builder.Build().Run();

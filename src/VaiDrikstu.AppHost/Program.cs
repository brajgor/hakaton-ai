var builder = DistributedApplication.CreateBuilder(args);

var postgresPassword = builder.AddParameter("postgres-password", "postgres", secret: true);

var postgres = builder
    .AddPostgres("postgis", password: postgresPassword, port: 5432)
    .WithImage("postgis/postgis", "17-3.5")
    .WithContainerRuntimeArgs("--platform", "linux/amd64")
    .WithDataVolume();

var database = postgres.AddDatabase("vaidrikstu");

var importer = builder
    .AddProject<Projects.VaiDrikstu_DataImporter>("data-importer")
    .WithReference(database)
    .WaitFor(database);

var api = builder
    .AddProject<Projects.VaiDrikstu_Api>("api")
    .WithReference(database)
    .WaitFor(database)
    .WaitForCompletion(importer)
    .WithExternalHttpEndpoints();

builder
    .AddViteApp("web", "../../web")
    .WithPnpm(install: false)
    .WithReference(api)
    .WaitFor(api)
    .WithEnvironment("VITE_API_URL", "http://localhost:5000")
    .WithExternalHttpEndpoints();

builder.Build().Run();

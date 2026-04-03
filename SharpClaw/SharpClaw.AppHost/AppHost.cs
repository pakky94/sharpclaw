var builder = DistributedApplication.CreateBuilder(args);

var dbUser = builder.AddParameter("dbUser", "sharpclaw");
var dbPassword = builder.AddParameter("dbPassword", "password", secret: true);

var db = builder.AddPostgres("sharpclaw-pg", dbUser, dbPassword)
    .WithImage("pgvector/pgvector:pg18-trixie")
    .WithHostPort(5532)
    .WithVolume("sharpclaw-pg-data", "/var/lib/postgresql")
    .WithLifetime(ContainerLifetime.Persistent)
    .AddDatabase("sharpclaw");

var sharpclaw = builder.AddProject<Projects.SharpClaw_API>("sharpclaw-api");

sharpclaw.WithReference(db).WaitFor(db);

var web = builder.AddViteApp("sharpclaw-web", Path.Join("..", "..", "sharpclaw-web"));

web.WithEnvironment("VITE_API_BASE_URL", sharpclaw.GetEndpoint("http"));

builder.Build().Run();

using SharpClaw.API.Agents;
using SharpClaw.API.Database;
using SharpClaw.API.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddTransient<DatabaseSeeder>();
builder.Services.AddSingleton<Repository>();
builder.Services.AddSingleton<FragmentsRepository>();
builder.Services.AddSingleton<FragmentEmbeddingService>();
builder.Services.AddHostedService<FragmentEmbeddingBackgroundService>();
builder.Services.Configure<LmStudioConfiguration>(builder.Configuration.GetSection("LmStudio"));
builder.Services.AddSingleton<ChatProvider>();
builder.Services.AddSingleton<Agent>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("WebClient", policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

var seeder = app.Services.GetRequiredService<DatabaseSeeder>();
await seeder.Seed();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors("WebClient");

ChatEndpoints.Register(app);
AgentEndpoints.Register(app);

app.Run();

using Microsoft.Extensions.Options;
using SharpClaw.API.Agents;
using SharpClaw.API.Agents.Workspace;
using SharpClaw.API.Database;
using SharpClaw.API.Database.Repositories;
using SharpClaw.API.Endpoints;
using SharpClaw.API.Web;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddTransient<DatabaseSeeder>();
builder.Services.AddSingleton<ChatRepository>();
builder.Services.AddSingleton<AgentsRepository>();
builder.Services.AddSingleton<FragmentsRepository>();
builder.Services.AddSingleton<WorkspaceRepository>();
builder.Services.AddSingleton<ApprovalService>();
builder.Services.AddSingleton<FragmentEmbeddingService>();
builder.Services.AddHostedService<FragmentEmbeddingBackgroundService>();
builder.Services.Configure<LmStudioConfiguration>(builder.Configuration.GetSection("LmStudio"));
builder.Services.Configure<BraveSearchConfiguration>(builder.Configuration.GetSection("WebSearch:Brave"));
builder.Services.AddSingleton<ChatProvider>();
builder.Services.AddSingleton<Agent>();

// Web search services
builder.Services.AddHttpClient<BraveSearchService>("BraveSearch", (sp, client) =>
{
    var config = sp.GetRequiredService<IOptions<BraveSearchConfiguration>>().Value;
    client.BaseAddress = new Uri(config.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<ISearchService>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var logger = sp.GetRequiredService<ILogger<BraveSearchService>>();
    var config = sp.GetRequiredService<IOptions<BraveSearchConfiguration>>().Value;
    return new BraveSearchService(httpClientFactory.CreateClient("BraveSearch"), Microsoft.Extensions.Options.Options.Create(config), logger);
});
builder.Services.AddHttpClient<WebFetchService>("WebFetch", (sp, client) =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("SharpClaw/1.0 (Web Content Fetcher)");
});
builder.Services.AddSingleton<IWebFetchService>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var logger = sp.GetRequiredService<ILogger<WebFetchService>>();
    return new WebFetchService(httpClientFactory.CreateClient("WebFetch"), logger);
});

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
app.UseDefaultFiles();
app.UseStaticFiles();

ChatEndpoints.Register(app);
AgentEndpoints.Register(app);
WorkspaceEndpoints.Register(app);
app.MapFallbackToFile("{*path:nonfile}", "index.html");

app.Run();

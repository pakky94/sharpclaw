using Microsoft.AspNetCore.WebSockets;
using Microsoft.Extensions.Options;
using SharpClaw.API.Agents;
using SharpClaw.API.Agents.Channels;
using SharpClaw.API.Agents.Channels.Discord;
using SharpClaw.API.Agents.ScheduledJobs;
using SharpClaw.API.Agents.Secrets;
using SharpClaw.API.Agents.Workspace;
using SharpClaw.API.Database;
using SharpClaw.API.Database.Repositories;
using SharpClaw.API.Endpoints;
using SharpClaw.API.Web;

var builder = WebApplication.CreateBuilder(args);
var debuggingEndpointsEnabled =
    builder.Configuration.GetValue<bool>("Debugging:Enabled")
    || builder.Configuration.GetValue<bool>("SHARPCLAW_DEBUGGING_ENDPOINTS_ENABLED");

builder.AddServiceDefaults();

builder.Services.AddTransient<DatabaseSeeder>();
builder.Services.AddSingleton<ChatRepository>();
builder.Services.AddSingleton<AgentsRepository>();
builder.Services.AddSingleton<FragmentsRepository>();
builder.Services.AddSingleton<WorkspaceRepository>();
builder.Services.AddSingleton<LocalWorkspaceExecutor>();
builder.Services.AddSingleton<IWorkspaceExecutionRouterFactory, WorkspaceExecutionRouterFactory>();
builder.Services.AddSingleton<BridgeConnectionManager>();
builder.Services.AddSingleton<ApprovalService>();
builder.Services.AddSingleton<FragmentEmbeddingService>();
builder.Services.AddHostedService<FragmentEmbeddingBackgroundService>();
builder.Services.AddSingleton<ScheduledJobRepository>();
builder.Services.AddSingleton<ChannelRepository>();
builder.Services.AddSingleton<SecretRepository>();
builder.Services.AddSingleton<SecretService>();
builder.Services.AddSingleton<ChannelRouter>();
builder.Services.AddSingleton<DiscordAdapter>();
builder.Services.AddHostedService<CronScheduler>();
builder.Services.Configure<LmStudioConfiguration>(builder.Configuration.GetSection("LmStudio"));
builder.Services.Configure<SearchProviderConfiguration>(builder.Configuration.GetSection("WebSearch"));
builder.Services.Configure<BraveSearchConfiguration>(builder.Configuration.GetSection("WebSearch:Brave"));
builder.Services.Configure<SearxngSearchConfiguration>(builder.Configuration.GetSection("WebSearch:Searxng"));
builder.Services.AddSingleton<ChatProvider>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<Agent>();

// Web search services
builder.Services.AddHttpClient<BraveSearchService>("BraveSearch", (sp, client) =>
{
    var config = sp.GetRequiredService<IOptions<BraveSearchConfiguration>>().Value;
    client.BaseAddress = new Uri(config.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<SearxngSearchService>("SearxngSearch", (sp, client) =>
{
    var config = sp.GetRequiredService<IOptions<SearxngSearchConfiguration>>().Value;
    client.BaseAddress = new Uri(config.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<ISearchService>(sp =>
{
    var providerConfig = sp.GetRequiredService<IOptions<SearchProviderConfiguration>>().Value;
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();

    if (string.Equals(providerConfig.ActiveProvider, "Searxng", StringComparison.OrdinalIgnoreCase))
    {
        var logger = sp.GetRequiredService<ILogger<SearxngSearchService>>();
        var config = sp.GetRequiredService<IOptions<SearxngSearchConfiguration>>().Value;
        return new SearxngSearchService(
            httpClientFactory.CreateClient("SearxngSearch"),
            Microsoft.Extensions.Options.Options.Create(config),
            logger);
    }

    if (string.Equals(providerConfig.ActiveProvider, "Brave", StringComparison.OrdinalIgnoreCase))
    {
        var logger = sp.GetRequiredService<ILogger<BraveSearchService>>();
        var config = sp.GetRequiredService<IOptions<BraveSearchConfiguration>>().Value;
        return new BraveSearchService(
            httpClientFactory.CreateClient("BraveSearch"),
            Microsoft.Extensions.Options.Options.Create(config),
            logger);
    }

    throw new InvalidOperationException(
        $"Unsupported web search provider '{providerConfig.ActiveProvider}'. Supported values: Brave, Searxng.");
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
builder.Services.AddWebSockets(c => {});

var app = builder.Build();

var seeder = app.Services.GetRequiredService<DatabaseSeeder>();
await seeder.Seed();

// Start channel adapters (Discord, etc.)
var channelRouter = app.Services.GetRequiredService<ChannelRouter>();
var discordAdapter = app.Services.GetRequiredService<DiscordAdapter>();
channelRouter.RegisterAdapter(discordAdapter);
_ = Task.Run(() => channelRouter.StartAsync());

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
app.UseWebSockets();

ChatEndpoints.Register(app);
AgentEndpoints.Register(app);
WorkspaceEndpoints.Register(app);
BridgeEndpoints.Register(app);
ScheduledJobEndpoints.Register(app);
ChannelEndpoints.Register(app);
SecretEndpoints.Register(app);
if (debuggingEndpointsEnabled)
    DebuggingEndpoints.Register(app);
app.MapFallbackToFile("{*path:nonfile}", "index.html");

app.Run();

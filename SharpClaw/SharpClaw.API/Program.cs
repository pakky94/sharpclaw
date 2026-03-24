using Microsoft.AspNetCore.Mvc;
using SharpClaw.API.Agents;
using SharpClaw.API.Database;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddTransient<DatabaseSeeder>();
builder.Services.Configure<LmStudioConfiguration>(builder.Configuration.GetSection("LmStudio"));
builder.Services.AddScoped<ChatProvider>();
builder.Services.AddScoped<Agent>();

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

app.MapPost("/chat", async (
    [FromBody] MessageRequest request,
    [FromServices] Agent agent
) =>
{
    var r = await agent.GetResponse(request.Message);
    return r;
});

app.Run();

public class MessageRequest
{
    public required string Message { get; set; }
}
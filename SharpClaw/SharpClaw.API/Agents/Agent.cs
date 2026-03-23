using Microsoft.Extensions.AI;
using SharpClaw.API.Database;

namespace SharpClaw.API.Agents;

public class Agent(ChatProvider chatProvider, IConfiguration configuration)
{
    public async Task<string> GetResponse(string prompt)
    {
        var agent = chatProvider.GetClient(new AgentExecutionContext
        {
            DbConnectionString = configuration.GetConnectionString("sharpclaw")!,
            AgentId = 1,
        });

        var response = await agent.GetResponse([
            new Message
            {
                Role = MessageRole.System,
                Content = DatabaseSeeder.AgentsMd,
            },
            new Message
            {
                Role = MessageRole.User,
                Content = prompt,
            }
        ], [
            AIFunctionFactory.Create(Tooling.ListFiles, "list_files", "Lists all files in your workspace"),
            AIFunctionFactory.Create(Tooling.ReadFile, "read_file", "Read a file from your workspace"),
        ]);
        return response;
    }
}
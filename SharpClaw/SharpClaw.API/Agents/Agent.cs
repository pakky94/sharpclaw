using Dapper;
using Microsoft.Extensions.AI;
using Npgsql;

namespace SharpClaw.API.Agents;

public class Agent(ChatProvider chatProvider, IConfiguration configuration)
{
    private static AgentExecutionContext? _context = null;

    public async Task<string> GetResponse(string prompt, long agentId = 1)
    {
        var agentMd = await GetAgentMd(agentId); // TODO: what to do if agentMd is null?

        _context ??= new AgentExecutionContext
        {
            DbConnectionString = configuration.GetConnectionString("sharpclaw")!,
            AgentId = 1,
            Messages =
            [
                new ChatMessage(ChatRole.System, agentMd),
            ],
        };

        _context.Messages.Add(new ChatMessage(ChatRole.User, prompt));

        var agent = chatProvider.GetClient(_context);

        var response = await agent.GetResponse([
            AIFunctionFactory.Create(Tooling.ListFiles, "list_files", "Lists all files in your workspace"),
            AIFunctionFactory.Create(Tooling.ReadFile, "read_file", "Read a file from your workspace"),
            AIFunctionFactory.Create(Tooling.WriteFile, "write_file", "Write a file in your workspace, overwriting if it exists"),
            AIFunctionFactory.Create(Tooling.DeleteFile, "delete_file", "Delete a file from your workspace"),
        ]);
        return response;
    }

    private async Task<string?> GetAgentMd(long agentId)
    {
        await using var connection = new NpgsqlConnection(configuration.GetConnectionString("sharpclaw"));

        var content = await connection.QueryFirstOrDefaultAsync<string>(
            """
            select d.content from agents a
            join agents_documents ad on a.id = ad.agent_id
            join documents d on d.id = ad.document_id
            where a.Id = @agentId and d.name = 'AGENTS.md';
            """,
            new
            {
                agentId,
            });

        return content;
    }
}
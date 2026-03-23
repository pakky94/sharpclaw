using Dapper;
using Npgsql;

namespace SharpClaw.API.Agents;

public class Tooling
{
    public static async Task<string[]> ListFiles(IServiceProvider serviceProvider)
    {
        Console.WriteLine($"Listing files");
        var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
        await using var connection = new NpgsqlConnection(ctx.DbConnectionString);

        var files = (await connection.QueryAsync<string>(
            """
            select d.name from agents a
            join agents_documents ad on a.id = ad.agent_id
            join documents d on d.id = ad.document_id
            where a.Id = @agentId
            """,
            new
            {
                ctx.AgentId,
            })).ToArray();

    Console.WriteLine($"Files found: {string.Join(", ", files)}");

        return files;
    }

    public static async Task<string> ReadFile(IServiceProvider serviceProvider, string path)
    {
        Console.WriteLine($"Reading file: {path}");
        var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
        await using var connection = new NpgsqlConnection(ctx.DbConnectionString);

        var content = await connection.QueryFirstAsync<string>(
            """
            select d.content from agents a
            join agents_documents ad on a.id = ad.agent_id
            join documents d on d.id = ad.document_id
            where a.Id = 1 and d.name = @path;
            """,
            new
            {
                ctx.AgentId,
                path,
            });

        Console.WriteLine($"Read content: {content}");

        return content;
    }
}
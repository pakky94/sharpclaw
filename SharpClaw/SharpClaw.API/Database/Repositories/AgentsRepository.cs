using Dapper;
using Npgsql;

namespace SharpClaw.API.Database.Repositories;

public class AgentsRepository(IConfiguration configuration)
{
    private string ConnectionString => configuration.GetConnectionString("sharpclaw")
                                       ?? throw new InvalidOperationException("Missing connection string 'sharpclaw'.");

    public async Task<IReadOnlyList<AgentConfig>> GetAgents()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<AgentConfig>(
            """
            select id as Id,
                   name as Name,
                   llm_model as LlmModel,
                   temperature as Temperature,
                   created_at as CreatedAt,
                   updated_at as UpdatedAt
            from agents
            order by id;
            """);

        return rows.ToArray();
    }

    public async Task<AgentConfig?> GetAgent(long agentId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QueryFirstOrDefaultAsync<AgentConfig>(
            """
            select id as Id,
                   name as Name,
                   llm_model as LlmModel,
                   temperature as Temperature,
                   created_at as CreatedAt,
                   updated_at as UpdatedAt
            from agents
            where id = @agentId;
            """,
            new { agentId });
    }

    public async Task<AgentConfig> CreateAgent(string name, string llmModel, float temperature)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleAsync<AgentConfig>(
            """
            insert into agents (name, llm_model, temperature)
            values (@name, @llmModel, @temperature)
            returning id as Id,
                      name as Name,
                      llm_model as LlmModel,
                      temperature as Temperature,
                      created_at as CreatedAt,
                      updated_at as UpdatedAt;
            """,
            new { name, llmModel, temperature });
    }

    public async Task<AgentConfig?> UpdateAgent(long agentId, string name, string llmModel, float temperature)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QueryFirstOrDefaultAsync<AgentConfig>(
            """
            update agents
            set name = @name,
                llm_model = @llmModel,
                temperature = @temperature,
                updated_at = now()
            where id = @agentId
            returning id as Id,
                      name as Name,
                      llm_model as LlmModel,
                      temperature as Temperature,
                      created_at as CreatedAt,
                      updated_at as UpdatedAt;
            """,
            new { agentId, name, llmModel, temperature });
    }
}

public record AgentConfig(long Id, string Name, string LlmModel, float Temperature, DateTime CreatedAt, DateTime UpdatedAt);

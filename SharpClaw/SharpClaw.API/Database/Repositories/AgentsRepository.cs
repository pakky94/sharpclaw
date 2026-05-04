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
                   soft_compact_threshold as SoftCompactThreshold,
                   hard_compact_threshold as HardCompactThreshold,
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
                   soft_compact_threshold as SoftCompactThreshold,
                   hard_compact_threshold as HardCompactThreshold,
                   created_at as CreatedAt,
                   updated_at as UpdatedAt
            from agents
            where id = @agentId;
            """,
            new { agentId });
    }

    public async Task<AgentConfig> CreateAgent(
        string name,
        string llmModel,
        float temperature,
        long softCompactThreshold,
        long hardCompactThreshold)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleAsync<AgentConfig>(
            """
            insert into agents (name, llm_model, temperature, soft_compact_threshold, hard_compact_threshold)
            values (@name, @llmModel, @temperature, @softCompactThreshold, @hardCompactThreshold)
            returning id as Id,
                      name as Name,
                      llm_model as LlmModel,
                      temperature as Temperature,
                      soft_compact_threshold as SoftCompactThreshold,
                      hard_compact_threshold as HardCompactThreshold,
                      created_at as CreatedAt,
                      updated_at as UpdatedAt;
            """,
            new { name, llmModel, temperature, softCompactThreshold, hardCompactThreshold });
    }

    public async Task<AgentConfig?> UpdateAgent(
        long agentId,
        string name,
        string llmModel,
        float temperature,
        long softCompactThreshold,
        long hardCompactThreshold)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QueryFirstOrDefaultAsync<AgentConfig>(
            """
            update agents
            set name = @name,
                llm_model = @llmModel,
                temperature = @temperature,
                soft_compact_threshold = @softCompactThreshold,
                hard_compact_threshold = @hardCompactThreshold,
                updated_at = now()
            where id = @agentId
            returning id as Id,
                      name as Name,
                      llm_model as LlmModel,
                      temperature as Temperature,
                      soft_compact_threshold as SoftCompactThreshold,
                      hard_compact_threshold as HardCompactThreshold,
                      created_at as CreatedAt,
                      updated_at as UpdatedAt;
            """,
            new { agentId, name, llmModel, temperature, softCompactThreshold, hardCompactThreshold });
    }
}

public record AgentConfig(
    long Id,
    string Name,
    string LlmModel,
    float Temperature,
    long SoftCompactThreshold,
    long HardCompactThreshold,
    DateTime CreatedAt,
    DateTime UpdatedAt);

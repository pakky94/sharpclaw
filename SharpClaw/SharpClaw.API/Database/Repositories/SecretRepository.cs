using Dapper;
using Npgsql;

namespace SharpClaw.API.Database.Repositories;

public class SecretRepository(IConfiguration configuration)
{
    private string ConnectionString => configuration.GetConnectionString("sharpclaw")!;

    public async Task<IReadOnlyList<SecretRow>> GetAll()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<SecretRow>(
            "select id, name, scope, owner_id, allow_bridge, created_at, updated_at from secrets order by name");
        return rows.ToArray();
    }

    public async Task<SecretRow?> GetById(long id)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleOrDefaultAsync<SecretRow>(
            "select * from secrets where id = @id", new { id });
    }

    public async Task<SecretRow?> GetByName(string name)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleOrDefaultAsync<SecretRow>(
            "select * from secrets where name = @name", new { name });
    }

    public async Task<SecretRow> Create(string name, string encryptedValue, string scope, long? ownerId, bool allowBridge = false)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleAsync<SecretRow>(
            """
            insert into secrets (name, encrypted_value, scope, owner_id, allow_bridge)
            values (@Name, @EncryptedValue, @Scope, @OwnerId, @AllowBridge)
            returning *
            """,
            new { Name = name, EncryptedValue = encryptedValue, Scope = scope, OwnerId = ownerId, AllowBridge = allowBridge });
    }

    public async Task<SecretRow?> Update(long id, string? encryptedValue, string? scope, long? ownerId, bool? allowBridge = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleOrDefaultAsync<SecretRow>(
            """
            update secrets
            set encrypted_value = coalesce(@EncryptedValue, encrypted_value),
                scope = coalesce(@Scope, scope),
                owner_id = coalesce(@OwnerId, owner_id),
                allow_bridge = coalesce(@AllowBridge, allow_bridge),
                updated_at = now()
            where id = @Id
            returning *
            """,
            new { Id = id, EncryptedValue = encryptedValue, Scope = scope, OwnerId = ownerId, AllowBridge = allowBridge });
    }

    public async Task<bool> Delete(long id)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var deleted = await connection.ExecuteAsync(
            "delete from secrets where id = @id", new { id });
        return deleted > 0;
    }

    /// <summary>
    /// Get encrypted secrets accessible to an agent (global + agent-scoped).
    /// </summary>
    public async Task<IReadOnlyList<SecretRow>> GetSecretsForAgent(long agentId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<SecretRow>(
            """
            select * from secrets
            where scope = 'global'
               or (scope = 'agent' and owner_id = @AgentId)
            order by name
            """,
            new { AgentId = agentId });
        return rows.ToArray();
    }

    public sealed class SecretRow
    {
        public long id { get; set; }
        public string name { get; set; } = string.Empty;
        public string encrypted_value { get; set; } = string.Empty;
        public string scope { get; set; } = "global";
        public long? owner_id { get; set; }
        public bool allow_bridge { get; set; }
        public DateTimeOffset created_at { get; set; }
        public DateTimeOffset updated_at { get; set; }
    }
}

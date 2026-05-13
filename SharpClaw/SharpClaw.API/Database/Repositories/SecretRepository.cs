using System.Security.Cryptography;
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
            "select id, name, scope, owner_id, created_at, updated_at from secrets order by name");
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

    public async Task<SecretRow> Create(string name, string encryptedValue, string scope, long? ownerId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleAsync<SecretRow>(
            """
            insert into secrets (name, encrypted_value, scope, owner_id)
            values (@Name, @EncryptedValue, @Scope, @OwnerId)
            returning *
            """,
            new { Name = name, EncryptedValue = encryptedValue, Scope = scope, OwnerId = ownerId });
    }

    public async Task<SecretRow?> Update(long id, string? encryptedValue, string? scope, long? ownerId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleOrDefaultAsync<SecretRow>(
            """
            update secrets
            set encrypted_value = coalesce(@EncryptedValue, encrypted_value),
                scope = coalesce(@Scope, scope),
                owner_id = coalesce(@OwnerId, owner_id),
                updated_at = now()
            where id = @Id
            returning *
            """,
            new { Id = id, EncryptedValue = encryptedValue, Scope = scope, OwnerId = ownerId });
    }

    public async Task<bool> Delete(long id)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var deleted = await connection.ExecuteAsync(
            "delete from secrets where id = @id", new { id });
        return deleted > 0;
    }

    public async Task<IReadOnlyList<SecretRow>> GetAllEncrypted()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<SecretRow>(
            "select * from secrets order by name");
        return rows.ToArray();
    }

    public sealed class SecretRow
    {
        public long id { get; set; }
        public string name { get; set; } = string.Empty;
        public string encrypted_value { get; set; } = string.Empty;
        public string scope { get; set; } = "global";
        public long? owner_id { get; set; }
        public DateTimeOffset created_at { get; set; }
        public DateTimeOffset updated_at { get; set; }
    }
}

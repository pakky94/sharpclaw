using Dapper;
using Npgsql;
using SharpClaw.API.Agents.Channels;

namespace SharpClaw.API.Database.Repositories;

public class ChannelRepository(IConfiguration configuration)
{
    private string ConnectionString => configuration.GetConnectionString("sharpclaw")!;

    public async Task<IReadOnlyList<Channel>> GetAll()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<ChannelRow>(
            "select * from channels order by name");
        return rows.Select(r => r.ToModel()).ToArray();
    }

    public async Task<Channel?> GetById(long id)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var row = await connection.QuerySingleOrDefaultAsync<ChannelRow>(
            "select * from channels where id = @id", new { id });
        return row?.ToModel();
    }

    public async Task<IReadOnlyList<Channel>> GetEnabledByType(string type)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<ChannelRow>(
            "select * from channels where type = @type and enabled = true order by name",
            new { type });
        return rows.Select(r => r.ToModel()).ToArray();
    }

    public async Task<Channel> Create(Channel channel)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var row = await connection.QuerySingleAsync<ChannelRow>(
            """
            insert into channels (name, type, agent_id, routing_mode, config, enabled)
            values (@Name, @Type, @AgentId, @RoutingMode, @Config::jsonb, @Enabled)
            returning *
            """,
            new
            {
                channel.Name,
                channel.Type,
                channel.AgentId,
                channel.RoutingMode,
                channel.Config,
                channel.Enabled,
            });
        return row.ToModel();
    }

    public async Task<Channel?> Update(long id, Channel channel)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var row = await connection.QuerySingleOrDefaultAsync<ChannelRow>(
            """
            update channels
            set name = @Name,
                type = @Type,
                agent_id = @AgentId,
                routing_mode = @RoutingMode,
                config = @Config::jsonb,
                enabled = @Enabled,
                updated_at = now()
            where id = @Id
            returning *
            """,
            new
            {
                channel.Id,
                channel.Name,
                channel.Type,
                channel.AgentId,
                channel.RoutingMode,
                channel.Config,
                channel.Enabled,
            });
        return row?.ToModel();
    }

    public async Task<bool> Delete(long id)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var deleted = await connection.ExecuteAsync(
            "delete from channels where id = @id", new { id });
        return deleted > 0;
    }

    public async Task<ChannelSessionRow?> GetChannelSession(long channelId, string identityId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleOrDefaultAsync<ChannelSessionRow>(
            "select * from channel_sessions where channel_id = @channelId and identity_id = @identityId",
            new { channelId, identityId });
    }

    public async Task<ChannelSessionRow?> GetAnyChannelSession(long channelId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleOrDefaultAsync<ChannelSessionRow>(
            "select * from channel_sessions where channel_id = @channelId limit 1",
            new { channelId });
    }

    public async Task<ChannelSessionRow> CreateChannelSession(long channelId, string identityId, Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleAsync<ChannelSessionRow>(
            """
            insert into channel_sessions (channel_id, identity_id, session_id)
            values (@ChannelId, @IdentityId, @SessionId)
            returning *
            """,
            new { ChannelId = channelId, IdentityId = identityId, SessionId = sessionId });
    }

    public async Task UpdateBroadcastCursor(long channelId, string identityId, long lastBroadcastSequence)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync(
            """
            update channel_sessions
            set last_broadcast_sequence = @LastBroadcastSequence
            where channel_id = @ChannelId and identity_id = @IdentityId
            """,
            new { ChannelId = channelId, IdentityId = identityId, LastBroadcastSequence = lastBroadcastSequence });
    }

    public async Task<IReadOnlyList<ChannelSessionInfo>> GetConnectedChannelsForSession(Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<ChannelSessionInfo>(
            """
            select cs.channel_id, cs.identity_id, cs.last_broadcast_sequence,
                   c.type as channel_type, c.config as channel_config, c.agent_id
            from channel_sessions cs
            join channels c on c.id = cs.channel_id
            where cs.session_id = @sessionId and c.enabled = true
            """,
            new { sessionId });
        return rows.ToArray();
    }

    public async Task<IReadOnlyList<Channel>> GetBySessionId(Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<ChannelRow>(
            """
            select distinct c.*
            from channels c
            join channel_sessions cs on cs.channel_id = c.id
            where cs.session_id = @sessionId
            """,
            new { sessionId });
        return rows.Select(r => r.ToModel()).ToArray();
    }

    private sealed class ChannelRow
    {
        public long id { get; set; }
        public string name { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty;
        public long agent_id { get; set; }
        public string routing_mode { get; set; } = "shared";
        public string config { get; set; } = "{}";
        public bool enabled { get; set; }
        public DateTimeOffset created_at { get; set; }
        public DateTimeOffset updated_at { get; set; }

        public Channel ToModel() => new()
        {
            Id = id,
            Name = name,
            Type = type,
            AgentId = agent_id,
            RoutingMode = routing_mode,
            Config = config,
            Enabled = enabled,
            CreatedAt = created_at,
            UpdatedAt = updated_at,
        };
    }

    public sealed class ChannelSessionRow
    {
        public long id { get; set; }
        public long channel_id { get; set; }
        public string identity_id { get; set; } = string.Empty;
        public Guid session_id { get; set; }
        public long last_broadcast_sequence { get; set; }
        public DateTimeOffset created_at { get; set; }
    }

    public sealed class ChannelSessionInfo
    {
        public long channel_id { get; set; }
        public string identity_id { get; set; } = string.Empty;
        public long last_broadcast_sequence { get; set; }
        public string channel_type { get; set; } = string.Empty;
        public string channel_config { get; set; } = "{}";
        public long agent_id { get; set; }
    }
}

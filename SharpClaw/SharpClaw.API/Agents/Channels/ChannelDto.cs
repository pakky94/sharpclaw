namespace SharpClaw.API.Agents.Channels;

public record ChannelDto(
    long Id,
    string Name,
    string Type,
    long AgentId,
    string RoutingMode,
    string Config,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record CreateChannelRequest(
    string Name,
    string Type,
    long AgentId,
    string? RoutingMode,
    string? Config,
    bool? Enabled
);

public record UpdateChannelRequest(
    string? Name,
    string? Type,
    long? AgentId,
    string? RoutingMode,
    string? Config,
    bool? Enabled
);

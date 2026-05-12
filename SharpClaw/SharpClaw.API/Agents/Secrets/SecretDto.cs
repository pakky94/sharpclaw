namespace SharpClaw.API.Agents.Secrets;

public record SecretDto(
    long Id,
    string Name,
    string Scope,
    long? OwnerId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record CreateSecretRequest(
    string Name,
    string Value,
    string Scope = "global",
    long? OwnerId = null
);

public record UpdateSecretRequest(
    string? Value = null,
    string? Scope = null,
    long? OwnerId = null
);

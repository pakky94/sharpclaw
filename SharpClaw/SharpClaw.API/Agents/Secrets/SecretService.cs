using System.Security.Cryptography;
using SharpClaw.API.Database.Repositories;

namespace SharpClaw.API.Agents.Secrets;

/// <summary>
/// Manages secrets: encrypts on write, decrypts on demand for command execution.
/// Secrets are injected as environment variables into commands (local and bridge).
/// </summary>
public class SecretService
{
    private readonly SecretRepository _repository;
    private readonly ILogger<SecretService> _logger;
    private readonly byte[] _key;

    public SecretService(
        SecretRepository repository,
        ILogger<SecretService> logger)
    {
        _repository = repository;
        _logger = logger;

        var keyPath = System.Environment.GetEnvironmentVariable("SHARPCLAW_SECRET_KEY_FILE")
                      ?? "/run/keys/sharpclaw-secret-key";

        if (!File.Exists(keyPath))
        {
            _logger.LogWarning("Secret key not found at {KeyPath}. Secrets will not be available.", keyPath);
            _key = Array.Empty<byte>();
            return;
        }

        var keyBase64 = File.ReadAllText(keyPath).Trim();
        _key = Convert.FromBase64String(keyBase64);
        _logger.LogInformation("Secret key loaded from {KeyPath}", keyPath);
    }

    /// <summary>
    /// Get decrypted environment variables for an agent's commands.
    /// Returns name→value pairs for all secrets accessible to the agent.
    /// </summary>
    public async Task<Dictionary<string, string>> GetAgentEnv(long agentId)
    {
        var env = new Dictionary<string, string>();

        if (_key.Length == 0) return env;

        var rows = await _repository.GetSecretsForAgent(agentId);

        foreach (var row in rows)
        {
            try
            {
                env[row.name] = Decrypt(row.encrypted_value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decrypt secret '{Name}'", row.name);
            }
        }

        return env;
    }

    /// <summary>
    /// Get decrypted environment variables safe for bridge transmission.
    /// Only includes secrets with allow_bridge = true.
    /// </summary>
    public async Task<Dictionary<string, string>> GetBridgeEnv(long agentId)
    {
        var env = new Dictionary<string, string>();

        if (_key.Length == 0) return env;

        var rows = await _repository.GetSecretsForAgent(agentId);

        foreach (var row in rows)
        {
            if (!row.allow_bridge) continue;

            try
            {
                env[row.name] = Decrypt(row.encrypted_value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decrypt secret '{Name}'", row.name);
            }
        }

        return env;
    }

    /// <summary>
    /// Encrypt a value and store it. Returns the created secret metadata (never the value).
    /// </summary>
    public async Task<SecretDto> AddSecret(string name, string value, string scope = "global", long? ownerId = null, bool allowBridge = false)
    {
        if (_key.Length == 0)
            throw new InvalidOperationException("Cannot add secret: encryption key not available");

        var encrypted = Encrypt(value);
        var row = await _repository.Create(name, encrypted, scope, ownerId, allowBridge);
        return Map(row);
    }

    /// <summary>
    /// Update a secret. Returns the updated metadata.
    /// </summary>
    public async Task<SecretDto?> UpdateSecret(long id, string? value = null, string? scope = null, long? ownerId = null, bool? allowBridge = null)
    {
        string? encrypted = null;
        if (value is not null)
        {
            if (_key.Length == 0)
                throw new InvalidOperationException("Cannot update secret: encryption key not available");
            encrypted = Encrypt(value);
        }

        var row = await _repository.Update(id, encrypted, scope, ownerId, allowBridge);
        if (row is null) return null;

        return Map(row);
    }

    /// <summary>
    /// Delete a secret by id. Returns true if deleted.
    /// </summary>
    public async Task<bool> DeleteSecret(long id)
    {
        var row = await _repository.GetById(id);
        if (row is null) return false;

        return await _repository.Delete(id);
    }

    /// <summary>
    /// List all secrets (metadata only, never values).
    /// </summary>
    public async Task<IReadOnlyList<SecretDto>> ListSecrets()
    {
        var rows = await _repository.GetAll();
        return rows.Select(Map).ToArray();
    }

    private string Encrypt(string plaintext)
    {
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    private string Decrypt(string encrypted)
    {
        var fullCipher = Convert.FromBase64String(encrypted);

        var iv = new byte[16];
        Buffer.BlockCopy(fullCipher, 0, iv, 0, 16);

        var cipherBytes = new byte[fullCipher.Length - 16];
        Buffer.BlockCopy(fullCipher, 16, cipherBytes, 0, cipherBytes.Length);

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return System.Text.Encoding.UTF8.GetString(plainBytes);
    }

    private static SecretDto Map(SecretRepository.SecretRow row) =>
        new(row.id, row.name, row.scope, row.owner_id, row.allow_bridge, row.created_at, row.updated_at);
}

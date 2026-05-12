using System.Security.Cryptography;
using SharpClaw.API.Database.Repositories;

namespace SharpClaw.API.Agents.Secrets;

/// <summary>
/// Manages secrets: encrypts on write, decrypts on startup,
/// writes decrypted values to /run/secrets/ for CLI tools and env vars.
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

        // Read the VM-local encryption key
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
    /// Decrypt all secrets and write them to /run/secrets/ for CLI tools.
    /// Called once on startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_key.Length == 0)
        {
            _logger.LogWarning("No secret key available, skipping secret initialization");
            return;
        }

        var secretsDir = "/run/secrets";
        Directory.CreateDirectory(secretsDir);

        var rows = await _repository.GetAllEncrypted();

        foreach (var row in rows)
        {
            try
            {
                var plaintext = Decrypt(row.encrypted_value);
                var filePath = Path.Combine(secretsDir, row.name);
                await File.WriteAllTextAsync(filePath, plaintext);

                // Set restrictive permissions (Linux only)
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(filePath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);

                _logger.LogInformation("Decrypted secret '{Name}' to {Path}", row.name, filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decrypt secret '{Name}'", row.name);
            }
        }
    }

    /// <summary>
    /// Get a decrypted secret value by name. Returns null if not found or key unavailable.
    /// </summary>
    public async Task<string?> GetSecretValue(string name)
    {
        if (_key.Length == 0) return null;

        var row = await _repository.GetByName(name);
        if (row is null) return null;

        try
        {
            return Decrypt(row.encrypted_value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt secret '{Name}'", name);
            return null;
        }
    }

    /// <summary>
    /// Encrypt a value and store it. Returns the created secret metadata (never the value).
    /// </summary>
    public async Task<SecretDto> AddSecret(string name, string value, string scope = "global", long? ownerId = null)
    {
        if (_key.Length == 0)
            throw new InvalidOperationException("Cannot add secret: encryption key not available");

        var encrypted = Encrypt(value);
        var row = await _repository.Create(name, encrypted, scope, ownerId);

        // Also write to /run/secrets/ immediately so it's available without restart
        var filePath = Path.Combine("/run/secrets", name);
        await File.WriteAllTextAsync(filePath, value);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        return Map(row);
    }

    /// <summary>
    /// Update a secret's value. Returns the updated metadata.
    /// </summary>
    public async Task<SecretDto?> UpdateSecret(long id, string? value = null, string? scope = null, long? ownerId = null)
    {
        string? encrypted = null;
        if (value is not null)
        {
            if (_key.Length == 0)
                throw new InvalidOperationException("Cannot update secret: encryption key not available");
            encrypted = Encrypt(value);
        }

        var row = await _repository.Update(id, encrypted, scope, ownerId);
        if (row is null) return null;

        // If value changed, update /run/secrets/
        if (value is not null)
        {
            var filePath = Path.Combine("/run/secrets", row.name);
            await File.WriteAllTextAsync(filePath, value);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return Map(row);
    }

    /// <summary>
    /// Delete a secret by id. Returns true if deleted.
    /// </summary>
    public async Task<bool> DeleteSecret(long id)
    {
        var row = await _repository.GetById(id);
        if (row is null) return false;

        var deleted = await _repository.Delete(id);
        if (deleted)
        {
            var filePath = Path.Combine("/run/secrets", row.name);
            if (File.Exists(filePath))
                File.Delete(filePath);
        }

        return deleted;
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

        // Prepend IV to ciphertext
        var result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    private string Decrypt(string encrypted)
    {
        var fullCipher = Convert.FromBase64String(encrypted);

        // Extract IV (first 16 bytes for AES)
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
        new(row.id, row.name, row.scope, row.owner_id, row.created_at, row.updated_at);
}

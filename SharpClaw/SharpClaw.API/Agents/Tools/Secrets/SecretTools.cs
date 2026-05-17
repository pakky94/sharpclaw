using Microsoft.Extensions.AI;
using SharpClaw.API.Agents.Secrets;

namespace SharpClaw.API.Agents.Tools.Secrets;

public static class SecretTools
{
    public static readonly AIFunction[] Functions =
    [
        AIFunctionFactory.Create(ListSecrets, "list_secrets",
            "List all stored secrets (names and metadata only — values are never shown)."),
        AIFunctionFactory.Create(AddSecret, "add_secret",
            "Store a new secret. The value will be encrypted and never echoed back."),
        AIFunctionFactory.Create(DeleteSecret, "delete_secret",
            "Delete a secret by ID."),
    ];

    private static async Task<object> ListSecrets(IServiceProvider serviceProvider)
    {
        var service = serviceProvider.GetRequiredService<SecretService>();
        var secrets = await service.ListSecrets();
        return new { secrets };
    }

    private static async Task<object> AddSecret(
        IServiceProvider serviceProvider,
        string name,
        string value,
        string scope = "global",
        long? owner_id = null,
        bool allow_bridge = false)
    {
        var service = serviceProvider.GetRequiredService<SecretService>();

        try
        {
            var secret = await service.AddSecret(name, value, scope, owner_id, allow_bridge);
            return new
            {
                created = true,
                secret = new { secret.Id, secret.Name, secret.Scope, secret.OwnerId, secret.AllowBridge }
            };
        }
        catch (InvalidOperationException ex)
        {
            return new { error = ex.Message };
        }
    }

    private static async Task<object> DeleteSecret(
        IServiceProvider serviceProvider,
        long id)
    {
        var service = serviceProvider.GetRequiredService<SecretService>();
        var deleted = await service.DeleteSecret(id);
        return new { deleted };
    }
}

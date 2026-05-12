using Microsoft.AspNetCore.Http.HttpResults;
using SharpClaw.API.Agents.Secrets;

namespace SharpClaw.API.Endpoints;

public static class SecretEndpoints
{
    public static void Register(WebApplication app)
    {
        var group = app.MapGroup("/secrets");

        group.MapGet("/", async (SecretService service) =>
        {
            var secrets = await service.ListSecrets();
            return Results.Ok(secrets);
        });

        group.MapPost("/", async (CreateSecretRequest request, SecretService service) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Name is required" });

            if (string.IsNullOrWhiteSpace(request.Value))
                return Results.BadRequest(new { error = "Value is required" });

            try
            {
                var secret = await service.AddSecret(
                    request.Name, request.Value, request.Scope, request.OwnerId);
                return Results.Created($"/secrets/{secret.Id}", secret);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPatch("/{id:long}", async (long id, UpdateSecretRequest request, SecretService service) =>
        {
            try
            {
                var secret = await service.UpdateSecret(id, request.Value, request.Scope, request.OwnerId);
                if (secret is null)
                    return Results.NotFound(new { error = "Secret not found" });
                return Results.Ok(secret);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/{id:long}", async (long id, SecretService service) =>
        {
            var deleted = await service.DeleteSecret(id);
            if (!deleted)
                return Results.NotFound(new { error = "Secret not found" });
            return Results.NoContent();
        });
    }
}

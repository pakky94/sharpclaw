using SharpClaw.Backend.Tests.Infrastructure;

namespace SharpClaw.Backend.Tests;

[Collection(SharpClawAppFixture.CollectionName)]
public sealed class SecretEndpointsTests(SharpClawAppFixture fixture)
{
    [Fact]
    public async Task ListSecrets_ReturnsEmpty_WhenNoSecretsExist()
    {
        await fixture.ResetStateAsync();

        using var result = await fixture.Api.ListSecretsAsync();
        var secrets = result.RootElement.GetProperty("secrets").EnumerateArray().ToArray();

        Assert.Empty(secrets);
    }

    [Fact]
    public async Task CreateSecret_WithValidData_ReturnsCreatedSecret()
    {
        await fixture.ResetStateAsync();

        using var result = await fixture.Api.CreateSecretAsync(
            name: "test-token",
            value: "secret-value-123");

        Assert.Equal("test-token", result.RootElement.GetProperty("name").GetString());
        Assert.Equal("global", result.RootElement.GetProperty("scope").GetString());
        Assert.True(result.RootElement.GetProperty("id").GetInt64() > 0);
        // Value must never be returned
        Assert.False(result.RootElement.TryGetProperty("value", out _));
        Assert.False(result.RootElement.TryGetProperty("encryptedValue", out _));
    }

    [Fact]
    public async Task CreateSecret_WithCustomScope_ReturnsCorrectScope()
    {
        await fixture.ResetStateAsync();

        using var result = await fixture.Api.CreateSecretAsync(
            name: "user-token",
            value: "secret-value",
            scope: "user",
            ownerId: 42);

        Assert.Equal("user", result.RootElement.GetProperty("scope").GetString());
        Assert.Equal(42, result.RootElement.GetProperty("ownerId").GetInt64());
    }

    [Fact]
    public async Task CreateSecret_WithMissingName_Returns400()
    {
        await fixture.ResetStateAsync();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.CreateSecretAsync(
                name: "",
                value: "secret-value"));

        Assert.Contains("400", exception.Message);
    }

    [Fact]
    public async Task CreateSecret_WithMissingValue_Returns400()
    {
        await fixture.ResetStateAsync();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.CreateSecretAsync(
                name: "test-token",
                value: ""));

        Assert.Contains("400", exception.Message);
    }

    [Fact]
    public async Task ListSecrets_ReturnsCreatedSecrets()
    {
        await fixture.ResetStateAsync();

        await fixture.Api.CreateSecretAsync(
            name: "github-token", value: "ghp_test1");
        await fixture.Api.CreateSecretAsync(
            name: "discord-token", value: "discord_test2");

        using var result = await fixture.Api.ListSecretsAsync();
        var secrets = result.RootElement.GetProperty("secrets").EnumerateArray().ToArray();

        Assert.Equal(2, secrets.Length);
        Assert.Contains(secrets, s => s.GetProperty("name").GetString() == "github-token");
        Assert.Contains(secrets, s => s.GetProperty("name").GetString() == "discord-token");
        // Values must never be in listing
        foreach (var s in secrets)
        {
            Assert.False(s.TryGetProperty("value", out _));
            Assert.False(s.TryGetProperty("encryptedValue", out _));
        }
    }

    [Fact]
    public async Task UpdateSecret_ChangesValue()
    {
        await fixture.ResetStateAsync();

        using var created = await fixture.Api.CreateSecretAsync(
            name: "update-test", value: "original-value");

        var id = created.RootElement.GetProperty("id").GetInt64();

        using var updated = await fixture.Api.UpdateSecretAsync(
            id, value: "new-value");

        Assert.Equal("update-test", updated.RootElement.GetProperty("name").GetString());
        // Value must not be returned
        Assert.False(updated.RootElement.TryGetProperty("value", out _));
    }

    [Fact]
    public async Task UpdateSecret_ChangesScope()
    {
        await fixture.ResetStateAsync();

        using var created = await fixture.Api.CreateSecretAsync(
            name: "scope-test", value: "some-value");

        var id = created.RootElement.GetProperty("id").GetInt64();

        using var updated = await fixture.Api.UpdateSecretAsync(
            id, scope: "user", ownerId: 99);

        Assert.Equal("user", updated.RootElement.GetProperty("scope").GetString());
        Assert.Equal(99, updated.RootElement.GetProperty("ownerId").GetInt64());
    }

    [Fact]
    public async Task UpdateSecret_NotFound_Returns404()
    {
        await fixture.ResetStateAsync();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.UpdateSecretAsync(9999, value: "ghost"));

        Assert.Contains("404", exception.Message);
    }

    [Fact]
    public async Task DeleteSecret_RemovesSecret()
    {
        await fixture.ResetStateAsync();

        using var created = await fixture.Api.CreateSecretAsync(
            name: "to-delete", value: "delete-me");

        var id = created.RootElement.GetProperty("id").GetInt64();

        using var deleteResult = await fixture.Api.DeleteSecretAsync(id);
        Assert.True(deleteResult.RootElement.GetProperty("deleted").GetBoolean());

        // Verify it's gone
        using var list = await fixture.Api.ListSecretsAsync();
        var secrets = list.RootElement.GetProperty("secrets").EnumerateArray().ToArray();
        Assert.DoesNotContain(secrets, s => s.GetProperty("id").GetInt64() == id);
    }

    [Fact]
    public async Task DeleteSecret_NotFound_Returns404()
    {
        await fixture.ResetStateAsync();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.DeleteSecretAsync(9999));

        Assert.Contains("404", exception.Message);
    }

    [Fact]
    public async Task CreateSecret_ValueNeverAppearsInResponse()
    {
        await fixture.ResetStateAsync();

        using var result = await fixture.Api.CreateSecretAsync(
            name: "no-leak", value: "super-secret-value-that-must-not-leak");

        var json = result.RootElement.GetRawText();
        Assert.DoesNotContain("super-secret-value-that-must-not-leak", json);
        Assert.DoesNotContain("encryptedValue", json);
    }

    [Fact]
    public async Task CreateSecret_WithAllowBridge_ReturnsCorrectFlag()
    {
        await fixture.ResetStateAsync();

        using var result = await fixture.Api.CreateSecretAsync(
            name: "bridge-token",
            value: "bridge-value",
            allowBridge: true);

        Assert.True(result.RootElement.GetProperty("allowBridge").GetBoolean());
    }

    [Fact]
    public async Task CreateSecret_WithoutAllowBridge_DefaultsToFalse()
    {
        await fixture.ResetStateAsync();

        using var result = await fixture.Api.CreateSecretAsync(
            name: "no-bridge-token",
            value: "no-bridge-value");

        Assert.False(result.RootElement.GetProperty("allowBridge").GetBoolean());
    }

    [Fact]
    public async Task UpdateSecret_ChangesAllowBridge()
    {
        await fixture.ResetStateAsync();

        using var created = await fixture.Api.CreateSecretAsync(
            name: "bridge-toggle", value: "test-value");

        var id = created.RootElement.GetProperty("id").GetInt64();
        Assert.False(created.RootElement.GetProperty("allowBridge").GetBoolean());

        // Toggle to true
        using var updated = await fixture.Api.UpdateSecretAsync(
            id, allowBridge: true);

        Assert.True(updated.RootElement.GetProperty("allowBridge").GetBoolean());
    }

    [Fact]
    public async Task CreateSecret_WithAgentScope_ReturnsCorrectOwner()
    {
        await fixture.ResetStateAsync();

        using var result = await fixture.Api.CreateSecretAsync(
            name: "agent-token",
            value: "agent-value",
            scope: "agent",
            ownerId: 1);

        Assert.Equal("agent", result.RootElement.GetProperty("scope").GetString());
        Assert.Equal(1, result.RootElement.GetProperty("ownerId").GetInt64());
    }

    [Fact]
    public async Task ListSecrets_ShowsAllowBridgeInListing()
    {
        await fixture.ResetStateAsync();

        await fixture.Api.CreateSecretAsync(
            name: "bridge-on", value: "v1", allowBridge: true);
        await fixture.Api.CreateSecretAsync(
            name: "bridge-off", value: "v2", allowBridge: false);

        using var result = await fixture.Api.ListSecretsAsync();
        var secrets = result.RootElement.GetProperty("secrets").EnumerateArray().ToArray();

        var bridgeOn = secrets.Single(s => s.GetProperty("name").GetString() == "bridge-on");
        Assert.True(bridgeOn.GetProperty("allowBridge").GetBoolean());

        var bridgeOff = secrets.Single(s => s.GetProperty("name").GetString() == "bridge-off");
        Assert.False(bridgeOff.GetProperty("allowBridge").GetBoolean());
    }
}

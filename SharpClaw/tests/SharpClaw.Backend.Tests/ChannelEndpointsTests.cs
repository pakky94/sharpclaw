using SharpClaw.Backend.Tests.Infrastructure;

namespace SharpClaw.Backend.Tests;

[Collection(SharpClawAppFixture.CollectionName)]
public sealed class ChannelEndpointsTests(SharpClawAppFixture fixture)
{
    [Fact]
    public async Task ListChannels_ReturnsEmpty_WhenNoChannelsExist()
    {
        await fixture.ResetStateAsync();

        using var result = await fixture.Api.ListChannelsAsync();
        var channels = result.RootElement.GetProperty("channels").EnumerateArray().ToArray();

        Assert.Empty(channels);
    }

    [Fact]
    public async Task CreateChannel_WithValidData_ReturnsCreatedChannel()
    {
        await fixture.ResetStateAsync();

        using var result = await fixture.Api.CreateChannelAsync(
            name: "Test Discord",
            type: "discord",
            agentId: 1,
            routingMode: "shared",
            config: "{\"bot_token\":\"test\"}");

        Assert.Equal("Test Discord", result.RootElement.GetProperty("name").GetString());
        Assert.Equal("discord", result.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, result.RootElement.GetProperty("agentId").GetInt64());
        Assert.Equal("shared", result.RootElement.GetProperty("routingMode").GetString());
        Assert.True(result.RootElement.GetProperty("enabled").GetBoolean());
        Assert.True(result.RootElement.GetProperty("id").GetInt64() > 0);
    }

    [Fact]
    public async Task CreateChannel_DefaultsToSharedRouting()
    {
        await fixture.ResetStateAsync();

        using var result = await fixture.Api.CreateChannelAsync(
            name: "Default Routing",
            type: "discord",
            agentId: 1);

        Assert.Equal("shared", result.RootElement.GetProperty("routingMode").GetString());
    }

    [Fact]
    public async Task CreateChannel_WithInvalidType_Returns400()
    {
        await fixture.ResetStateAsync();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.CreateChannelAsync(
                name: "Bad Type",
                type: "whatsapp",
                agentId: 1));

        Assert.Contains("400", exception.Message);
    }

    [Fact]
    public async Task CreateChannel_WithInvalidRoutingMode_Returns400()
    {
        await fixture.ResetStateAsync();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.CreateChannelAsync(
                name: "Bad Routing",
                type: "discord",
                agentId: 1,
                routingMode: "per_server"));

        Assert.Contains("400", exception.Message);
    }

    [Fact]
    public async Task CreateChannel_WithNonexistentAgent_Returns400()
    {
        await fixture.ResetStateAsync();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.CreateChannelAsync(
                name: "Bad Agent",
                type: "discord",
                agentId: 9999));

        Assert.Contains("400", exception.Message);
    }

    [Fact]
    public async Task CreateChannel_WithMissingName_Returns400()
    {
        await fixture.ResetStateAsync();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.CreateChannelAsync(
                name: "",
                type: "discord",
                agentId: 1));

        Assert.Contains("400", exception.Message);
    }

    [Fact]
    public async Task CreateChannel_WithDisabledFlag_ReturnsDisabledChannel()
    {
        await fixture.ResetStateAsync();

        using var result = await fixture.Api.CreateChannelAsync(
            name: "Disabled Channel",
            type: "discord",
            agentId: 1,
            enabled: false);

        Assert.False(result.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task ListChannels_ReturnsCreatedChannels()
    {
        await fixture.ResetStateAsync();

        await fixture.Api.CreateChannelAsync(
            name: "Channel A", type: "discord", agentId: 1);
        await fixture.Api.CreateChannelAsync(
            name: "Channel B", type: "telegram", agentId: 1);

        using var result = await fixture.Api.ListChannelsAsync();
        var channels = result.RootElement.GetProperty("channels").EnumerateArray().ToArray();

        Assert.Equal(2, channels.Length);
        Assert.Contains(channels, c => c.GetProperty("name").GetString() == "Channel A");
        Assert.Contains(channels, c => c.GetProperty("name").GetString() == "Channel B");
    }

    [Fact]
    public async Task UpdateChannel_ChangesFields()
    {
        await fixture.ResetStateAsync();

        using var created = await fixture.Api.CreateChannelAsync(
            name: "Original", type: "discord", agentId: 1);

        var id = created.RootElement.GetProperty("id").GetInt64();

        using var updated = await fixture.Api.UpdateChannelAsync(
            id,
            name: "Updated",
            routingMode: "per_user",
            enabled: false);

        Assert.Equal("Updated", updated.RootElement.GetProperty("name").GetString());
        Assert.Equal("per_user", updated.RootElement.GetProperty("routingMode").GetString());
        Assert.False(updated.RootElement.GetProperty("enabled").GetBoolean());
        // Type should be unchanged
        Assert.Equal("discord", updated.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task UpdateChannel_WithInvalidType_Returns400()
    {
        await fixture.ResetStateAsync();

        using var created = await fixture.Api.CreateChannelAsync(
            name: "Valid", type: "discord", agentId: 1);

        var id = created.RootElement.GetProperty("id").GetInt64();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.UpdateChannelAsync(id, type: "whatsapp"));

        Assert.Contains("400", exception.Message);
    }

    [Fact]
    public async Task UpdateChannel_NotFound_Returns404()
    {
        await fixture.ResetStateAsync();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.UpdateChannelAsync(9999, name: "Ghost"));

        Assert.Contains("404", exception.Message);
    }

    [Fact]
    public async Task DeleteChannel_RemovesChannel()
    {
        await fixture.ResetStateAsync();

        using var created = await fixture.Api.CreateChannelAsync(
            name: "To Delete", type: "discord", agentId: 1);

        var id = created.RootElement.GetProperty("id").GetInt64();

        using var deleteResult = await fixture.Api.DeleteChannelAsync(id);
        Assert.True(deleteResult.RootElement.GetProperty("deleted").GetBoolean());

        // Verify it's gone
        using var list = await fixture.Api.ListChannelsAsync();
        var channels = list.RootElement.GetProperty("channels").EnumerateArray().ToArray();
        Assert.DoesNotContain(channels, c => c.GetProperty("id").GetInt64() == id);
    }

    [Fact]
    public async Task DeleteChannel_NotFound_Returns404()
    {
        await fixture.ResetStateAsync();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.DeleteChannelAsync(9999));

        Assert.Contains("404", exception.Message);
    }
}

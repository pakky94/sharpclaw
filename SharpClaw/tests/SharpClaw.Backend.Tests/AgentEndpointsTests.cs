using SharpClaw.Backend.Tests.Infrastructure;

namespace SharpClaw.Backend.Tests;

[Collection(SharpClawAppFixture.CollectionName)]
public sealed class AgentEndpointsTests(SharpClawAppFixture fixture)
{
    [Fact]
    public async Task GetAgents_ReturnsSeededMainAgent()
    {
        await fixture.ResetStateAsync();

        using var payload = await fixture.Api.GetAgentsAsync();
        var agents = payload.RootElement.GetProperty("agents").EnumerateArray().ToArray();

        Assert.NotEmpty(agents);
        Assert.Contains(agents, agent =>
            agent.GetProperty("id").GetInt64() == 1 &&
            agent.GetProperty("name").GetString() == "Main");
    }
}

using SharpClaw.Backend.Tests.Infrastructure;

namespace SharpClaw.Backend.Tests;

[Collection(SharpClawAppFixture.CollectionName)]
public sealed class SessionEndpointsTests(SharpClawAppFixture fixture)
{
    [Fact]
    public async Task CreateSession_WithName_PersistsNameAndSidebarVisibility()
    {
        await fixture.ResetStateAsync();

        var sessionId = await fixture.Api.CreateSessionAsync(agentId: 1, name: "Planning");

        using var sessions = await fixture.Api.GetSessionsAsync(agentId: 1);
        var row = sessions.RootElement.GetProperty("sessions")
            .EnumerateArray()
            .Single(x => x.GetProperty("sessionId").GetGuid() == sessionId);

        Assert.Equal("Planning", row.GetProperty("name").GetString());
        Assert.True(row.GetProperty("visibleInSidebar").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("updatedAt").GetString()));
    }

    [Fact]
    public async Task RenameSession_UpdatesName_AndOrderingUsesUpdatedAt()
    {
        await fixture.ResetStateAsync();

        var olderSessionId = await fixture.Api.CreateSessionAsync(agentId: 1, name: "Older");
        await Task.Delay(50);
        var newerSessionId = await fixture.Api.CreateSessionAsync(agentId: 1, name: "Newer");

        using (var beforeRename = await fixture.Api.GetSessionsAsync(agentId: 1))
        {
            var ordered = beforeRename.RootElement.GetProperty("sessions").EnumerateArray().ToArray();
            Assert.Equal(newerSessionId, ordered[0].GetProperty("sessionId").GetGuid());
            Assert.Equal(olderSessionId, ordered[1].GetProperty("sessionId").GetGuid());
        }

        await fixture.Api.RenameSessionAsync(olderSessionId, "Renamed older");

        using var afterRename = await fixture.Api.GetSessionsAsync(agentId: 1);
        var reordered = afterRename.RootElement.GetProperty("sessions").EnumerateArray().ToArray();

        Assert.Equal(olderSessionId, reordered[0].GetProperty("sessionId").GetGuid());
        Assert.Equal("Renamed older", reordered[0].GetProperty("name").GetString());
        Assert.Equal(newerSessionId, reordered[1].GetProperty("sessionId").GetGuid());
    }
}

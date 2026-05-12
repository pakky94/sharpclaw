using System.Text.Json;
using SharpClaw.Backend.Tests.Infrastructure;

namespace SharpClaw.Backend.Tests;

[Collection(SharpClawAppFixture.CollectionName)]
public sealed class SessionTagTests(SharpClawAppFixture fixture)
{
    [Fact]
    public async Task CreateSession_WithTag_ReturnsSessionWithTag()
    {
        await fixture.ResetStateAsync();

        var sessionId = await fixture.Api.CreateSessionAsync(agentId: 1, tag: "main");

        using var sessions = await fixture.Api.GetSessionsAsync(1);
        var session = FindSession(sessions, sessionId);

        Assert.Equal("main", session.GetProperty("tag").GetString());
    }

    [Fact]
    public async Task CreateSession_TagCollision_UnlinksOldSession()
    {
        await fixture.ResetStateAsync();

        var sessionA = await fixture.Api.CreateSessionAsync(agentId: 1, tag: "main");
        var sessionB = await fixture.Api.CreateSessionAsync(agentId: 1, tag: "main");

        using var sessions = await fixture.Api.GetSessionsAsync(1);
        var a = FindSession(sessions, sessionA);
        var b = FindSession(sessions, sessionB);

        Assert.Equal(JsonValueKind.Null, a.GetProperty("tag").ValueKind);
        Assert.Equal("main", b.GetProperty("tag").GetString());
    }

    [Fact]
    public async Task SetSessionTag_UpdatesTag()
    {
        await fixture.ResetStateAsync();

        var sessionId = await fixture.Api.CreateSessionAsync(agentId: 1);
        await fixture.Api.SetSessionTagAsync(sessionId, "main");

        using var sessions = await fixture.Api.GetSessionsAsync(1);
        var session = FindSession(sessions, sessionId);

        Assert.Equal("main", session.GetProperty("tag").GetString());
    }

    [Fact]
    public async Task SetSessionTag_ClearTag()
    {
        await fixture.ResetStateAsync();

        var sessionId = await fixture.Api.CreateSessionAsync(agentId: 1, tag: "main");
        await fixture.Api.SetSessionTagAsync(sessionId, "");

        using var sessions = await fixture.Api.GetSessionsAsync(1);
        var session = FindSession(sessions, sessionId);

        Assert.Equal(JsonValueKind.Null, session.GetProperty("tag").ValueKind);
    }

    [Fact]
    public async Task SetSessionTag_Collision_UnlinksOld()
    {
        await fixture.ResetStateAsync();

        var sessionA = await fixture.Api.CreateSessionAsync(agentId: 1);
        var sessionB = await fixture.Api.CreateSessionAsync(agentId: 1);

        await fixture.Api.SetSessionTagAsync(sessionA, "main");
        await fixture.Api.SetSessionTagAsync(sessionB, "main");

        using var sessions = await fixture.Api.GetSessionsAsync(1);
        var a = FindSession(sessions, sessionA);
        var b = FindSession(sessions, sessionB);

        Assert.Equal(JsonValueKind.Null, a.GetProperty("tag").ValueKind);
        Assert.Equal("main", b.GetProperty("tag").GetString());
    }

    [Fact]
    public async Task GetSessions_IncludesTag()
    {
        await fixture.ResetStateAsync();

        var sessionId = await fixture.Api.CreateSessionAsync(agentId: 1, tag: "test-tag");

        using var sessions = await fixture.Api.GetSessionsAsync(1);
        var session = FindSession(sessions, sessionId);

        Assert.True(session.TryGetProperty("tag", out var tagProp));
        Assert.Equal("test-tag", tagProp.GetString());
    }

    [Fact]
    public async Task RenameSession_KeepsTag()
    {
        await fixture.ResetStateAsync();

        var sessionId = await fixture.Api.CreateSessionAsync(agentId: 1, tag: "main");
        await fixture.Api.RenameSessionAsync(sessionId, "New Name");

        using var sessions = await fixture.Api.GetSessionsAsync(1);
        var session = FindSession(sessions, sessionId);

        Assert.Equal("New Name", session.GetProperty("name").GetString());
        Assert.Equal("main", session.GetProperty("tag").GetString());
    }

    private static JsonElement FindSession(JsonDocument doc, Guid sessionId)
    {
        foreach (var s in doc.RootElement.GetProperty("sessions").EnumerateArray())
        {
            if (s.GetProperty("sessionId").GetGuid() == sessionId)
                return s;
        }
        throw new InvalidOperationException($"Session {sessionId} not found in response.");
    }
}
